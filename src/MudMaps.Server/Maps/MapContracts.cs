namespace MudMaps.Server.Maps;

// The JSON the map apps read. Same shape as tools/build_data.py's mud-v2.json.

public sealed record HoodDto(string Key, string Label, string Repo, int Size, int Prs);

public sealed record RoadDto(string A, string B, int W);

public sealed record ClusterDto(string Id, string Hood, int N, string Dir, IReadOnlyList<string> Top, int Prs, string Name);

public sealed record RepoDto(string Name, string FullName, string Url, int Stars, int Prs, int Merged, int Nodes, string? Built,
    RepoHighlightsDto? Highlights = null);

/// <summary>The headline facts shown when someone clicks a repository.</summary>
public sealed record RepoHighlightsDto(
    string? Description,
    string? Version,
    string? LatestRelease,
    DateTimeOffset? LatestReleaseAt,
    string? LatestReleaseUrl,
    DateTimeOffset? LastPrAt,
    DateTimeOffset? LastCommitAt,
    string? TopAuthor,
    int TopAuthorPrs,
    int TopAuthorMerged,
    int Contributors,
    int OpenPrs,
    int OpenIssues,
    int Forks,
    string? License);

public sealed record HeadlineDto(int Prs, int Merged);

public sealed record OtherRepoDto(string Name, string FullName, int Prs, int Stars);

public sealed record MapDto(
    HeadlineDto Headline,
    int ExtensionsPrs,
    IReadOnlyList<OtherRepoDto> Others,
    string GeneratedAt,
    int WindowDays,
    IReadOnlyList<RepoDto> Repos,
    IReadOnlyList<HoodDto> Hoods,
    IReadOnlyList<RoadDto> Roads,
    IReadOnlyList<ClusterDto> Clusters,
    bool PrsLive);

/// <summary>One pull request: when it merged, and the files it touched.</summary>
public sealed record PullRequestInfo(int Number, DateTimeOffset CreatedAt, DateTimeOffset? MergedAt, IReadOnlyList<string> Files, string? Author = null);

/// <summary>Pull requests and stars for one repository.</summary>
public sealed record RepoActivity(string FullName, string Url, int Stars, IReadOnlyList<PullRequestInfo> PullRequests, RepoFacts? Facts = null);

/// <summary>Repository facts that are not about the pull request window.</summary>
public sealed record RepoFacts(
    string? Description,
    string? LatestTag,
    string? LatestRelease,
    DateTimeOffset? LatestReleaseAt,
    string? LatestReleaseUrl,
    DateTimeOffset? LastPrAt,
    DateTimeOffset? LastCommitAt,
    int OpenPrs,
    int OpenIssues,
    int Forks,
    string? License);
