using System.Text.Json;
using System.Text.RegularExpressions;

namespace MudMaps.Server.GitHub;

/// <summary>
/// One open pull request as cached for the PR tools. Built from GitHub's answer with every text field cleaned and
/// capped: no bodies, no URLs from GitHub, logins checked.
/// </summary>
public sealed record OpenPr(
    int Number,
    string Title,
    string Author,
    string BaseRef,
    bool IsDraft,
    string ReviewDecision,
    string Ci,
    DateTimeOffset UpdatedAt,
    string Mergeable,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Files);

/// <summary>A repository's open pull requests from one refresh.</summary>
public sealed record RepoOpenPrs(string FullName, string? DefaultBranch, IReadOnlyList<OpenPr> Prs, DateTimeOffset FetchedAt);

/// <summary>Every repository's open pull requests; replaced whole on each refresh, never mutated.</summary>
public sealed record OpenPrSnapshot(DateTimeOffset? GeneratedAt, IReadOnlyDictionary<string, RepoOpenPrs> Repos)
{
    public static readonly OpenPrSnapshot Empty = new(null, new Dictionary<string, RepoOpenPrs>());
}

/// <summary>Read-only access to the cached open pull requests. The PR tools depend on this and nothing else from GitHub.</summary>
public interface IPrCacheSnapshot
{
    OpenPrSnapshot OpenPrs { get; }
}

public static partial class OpenPrParser
{
    public const int MaxTitle = 200;
    public const int MaxLabels = 10;
    public const int MaxLabel = 50;
    public const int MaxPath = 300;

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,39}(\[bot\])?$")]
    private static partial Regex Login();

    /// <summary>graphify's <c>_parse_ci</c> on the rollup's overall state.</summary>
    public static string Ci(string? rollupState) => rollupState switch
    {
        "FAILURE" or "ERROR" => "FAILURE",
        "PENDING" or "EXPECTED" => "PENDING",
        "SUCCESS" => "SUCCESS",
        _ => "NONE",
    };

    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > max ? s[..max] : s;
    }

    public static string Author(JsonElement author) =>
        author.ValueKind == JsonValueKind.Object && author.TryGetProperty("login", out var l) && l.GetString() is { } login && Login().IsMatch(login)
            ? login
            : "?";

    /// <summary>Reads one repository's open-PR GraphQL answer (<c>data.repository</c>).</summary>
    public static RepoOpenPrs Parse(string fullName, JsonElement repo, DateTimeOffset fetchedAt)
    {
        var defaultBranch = repo.TryGetProperty("defaultBranchRef", out var d) && d.ValueKind == JsonValueKind.Object
            ? Clean(d.GetProperty("name").GetString(), 100)
            : null;
        var prs = new List<OpenPr>();
        foreach (var n in repo.GetProperty("pullRequests").GetProperty("nodes").EnumerateArray())
        {
            var rollup = n.GetProperty("commits").GetProperty("nodes") is { ValueKind: JsonValueKind.Array } commits && commits.GetArrayLength() > 0
                && commits[0].GetProperty("commit").GetProperty("statusCheckRollup") is { ValueKind: JsonValueKind.Object } r
                ? r.GetProperty("state").GetString()
                : null;
            prs.Add(new OpenPr(
                n.GetProperty("number").GetInt32(),
                Clean(n.GetProperty("title").GetString(), MaxTitle),
                Author(n.GetProperty("author")),
                Clean(n.GetProperty("baseRefName").GetString(), 100),
                n.GetProperty("isDraft").GetBoolean(),
                n.GetProperty("reviewDecision").ValueKind == JsonValueKind.String ? Clean(n.GetProperty("reviewDecision").GetString(), 40) : "",
                Ci(rollup),
                n.GetProperty("updatedAt").GetDateTimeOffset(),
                n.TryGetProperty("mergeable", out var m) && m.ValueKind == JsonValueKind.String ? Clean(m.GetString(), 20) : "UNKNOWN",
                n.GetProperty("labels").GetProperty("nodes").EnumerateArray()
                    .Select(l => Clean(l.GetProperty("name").GetString(), MaxLabel)).Where(l => l.Length > 0).Take(MaxLabels).ToList(),
                n.GetProperty("files").GetProperty("nodes").EnumerateArray()
                    .Select(f => Clean(f.GetProperty("path").GetString(), MaxPath)).Where(f => f.Length > 0).Take(100).ToList()));
        }
        return new RepoOpenPrs(fullName, defaultBranch, prs, fetchedAt);
    }
}
