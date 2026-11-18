using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MudMaps.Server.Maps;

namespace MudMaps.Server.GitHub;

/// <summary>Reads public pull request activity from the GitHub GraphQL API. Read-only.</summary>
public sealed class GitHubActivityClient(HttpClient http, ILogger<GitHubActivityClient> log)
{
    private const string Query = """
        query($owner: String!, $name: String!, $cursor: String) {
          repository(owner: $owner, name: $name) {
            url
            stargazerCount
            description
            forkCount
            licenseInfo { spdxId }
            issues(states: OPEN) { totalCount }
            openPrs: pullRequests(states: OPEN) { totalCount }
            latestRelease { tagName publishedAt url }
            refs(refPrefix: "refs/tags/", first: 1, orderBy: {field: TAG_COMMIT_DATE, direction: DESC}) { nodes { name } }
            defaultBranchRef { target { ... on Commit { committedDate } } }
            pullRequests(first: 50, after: $cursor, orderBy: {field: CREATED_AT, direction: DESC}) {
              pageInfo { hasNextPage endCursor }
              nodes { number createdAt mergedAt author { __typename login } files(first: 100) { nodes { path } } }
            }
          }
        }
        """;

    private const int MaxPages = 12;

    /// <summary>When GitHub says the token expires, from the last response; null if it never expires or is unknown.</summary>
    public DateTimeOffset? TokenExpiresAt { get; private set; }

    /// <summary>Parses GitHub's "github-authentication-token-expiration" header, e.g. "2027-11-01 05:00:00 UTC".</summary>
    public static DateTimeOffset? ParseTokenExpiry(string? header) =>
        header is not null && DateTimeOffset.TryParseExact(header.Replace(" UTC", "Z").Replace(' ', 'T'), "yyyy-MM-dd'T'HH:mm:ssZ",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    /// <summary>Pull requests created since <paramref name="since"/>, newest first, plus stars.</summary>
    public async Task<RepoActivity> GetActivityAsync(string fullName, string token, DateTimeOffset since, CancellationToken ct)
    {
        var (owner, name) = Split(fullName);
        var prs = new List<PullRequestInfo>();
        string? cursor = null;
        string url = $"https://github.com/{fullName}";
        var stars = 0;
        RepoFacts? facts = null;
        DateTimeOffset? lastPr = null;
        for (var page = 0; page < MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = JsonContent.Create(new { query = Query, variables = new { owner, name, cursor } }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            if (response.Headers.TryGetValues("github-authentication-token-expiration", out var expiry))
            {
                TokenExpiresAt = ParseTokenExpiry(expiry.FirstOrDefault());
            }
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("errors", out var errors))
            {
                throw new InvalidOperationException($"GitHub GraphQL error for {fullName}: {errors[0].GetProperty("message").GetString()}");
            }
            var repo = doc.RootElement.GetProperty("data").GetProperty("repository");
            url = repo.GetProperty("url").GetString() ?? url;
            stars = repo.GetProperty("stargazerCount").GetInt32();
            facts ??= ReadFacts(repo);
            var conn = repo.GetProperty("pullRequests");
            var reachedOld = false;
            foreach (var n in conn.GetProperty("nodes").EnumerateArray())
            {
                var created = n.GetProperty("createdAt").GetDateTimeOffset();
                lastPr ??= created;
                if (created < since) { reachedOld = true; break; }
                DateTimeOffset? merged = n.GetProperty("mergedAt").ValueKind == JsonValueKind.String ? n.GetProperty("mergedAt").GetDateTimeOffset() : null;
                var files = n.GetProperty("files").GetProperty("nodes").EnumerateArray().Select(f => f.GetProperty("path").GetString()!).ToList();
                // Bots (Renovate, Dependabot) open PRs too, but "most pull requests" is about people.
                var who = n.GetProperty("author");
                var author = who.ValueKind == JsonValueKind.Object && who.GetProperty("__typename").GetString() != "Bot"
                    ? who.GetProperty("login").GetString()
                    : null;
                prs.Add(new PullRequestInfo(n.GetProperty("number").GetInt32(), created, merged, files, author));
            }
            var info = conn.GetProperty("pageInfo");
            if (reachedOld || !info.GetProperty("hasNextPage").GetBoolean()) break;
            cursor = info.GetProperty("endCursor").GetString();
        }
        log.LogInformation("GitHub: {Repo} has {Count} pull requests in the window", fullName, prs.Count);
        return new RepoActivity(fullName, url, stars, prs, facts is null ? null : facts with { LastPrAt = lastPr });
    }

    private static RepoFacts ReadFacts(JsonElement repo)
    {
        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static DateTimeOffset? Date(JsonElement e, string name) =>
            Str(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

        var release = repo.GetProperty("latestRelease");
        var tags = repo.GetProperty("refs").GetProperty("nodes");
        var commit = repo.GetProperty("defaultBranchRef");
        var target = commit.ValueKind == JsonValueKind.Object ? commit.GetProperty("target") : default;
        return new RepoFacts(
            Str(repo, "description"),
            tags.GetArrayLength() > 0 ? Str(tags[0], "name") : null,
            Str(release, "tagName"),
            Date(release, "publishedAt"),
            // Rendered as a link, so only GitHub release pages get through.
            Str(release, "url") is { } u && u.StartsWith("https://github.com/", StringComparison.Ordinal) ? u : null,
            null,
            Date(target, "committedDate"),
            repo.GetProperty("openPrs").GetProperty("totalCount").GetInt32(),
            repo.GetProperty("issues").GetProperty("totalCount").GetInt32(),
            repo.GetProperty("forkCount").GetInt32(),
            repo.GetProperty("licenseInfo") is { ValueKind: JsonValueKind.Object } l ? Str(l, "spdxId") : null);
    }

    private static (string Owner, string Name) Split(string fullName)
    {
        var parts = fullName.Split('/');
        if (parts.Length != 2) throw new ArgumentException($"Expected owner/name, got '{fullName}'.", nameof(fullName));
        return (parts[0], parts[1]);
    }
}
