namespace MudMaps.Shared;

/// <summary>One named area of the code, such as "Data Grid", grouped from Graphify clusters.</summary>
/// <param name="TestSize">How many of the area's items are test code.</param>
public sealed record Hood(string Key, string Label, string Repo, int Size, int Prs, int TestSize = 0);

/// <summary>How often two areas call each other.</summary>
public sealed record Road(string A, string B, int W);

/// <summary>One Graphify cluster inside an area, with its most connected code items.</summary>
/// <param name="TestN">How many of the cluster's items are test code.</param>
/// <param name="Path">The cluster's main folder, repository-relative.</param>
public sealed record Cluster(string Id, string Hood, int N, string Dir, IReadOnlyList<string> Top, int Prs = 0, string? Name = null,
    int TestN = 0, string? Path = null, IReadOnlyList<int>? PrNumbers = null);

/// <summary>A Mud repository that has no code map yet.</summary>
public sealed record OtherRepo(string Name, string FullName, int Prs, int Stars);

public sealed record Headline(int Prs, int Merged);

/// <summary>A mapped repository with its pull request activity (data v2).</summary>
public sealed record RepoInfo(string Name, string FullName, string Url, int Stars, int Prs, int Merged, int Nodes,
    RepoHighlights? Highlights = null, int TestNodes = 0, string? LicenseUrl = null);

/// <summary>Live repository facts from the service (absent in the baked-in snapshot).</summary>
public sealed record RepoHighlights(
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

public sealed record MapData(
    string GeneratedAt,
    int WindowDays,
    Headline Headline,
    int ExtensionsPrs,
    IReadOnlyList<Hood> Hoods,
    IReadOnlyList<Road> Roads,
    IReadOnlyList<Cluster> Clusters,
    IReadOnlyList<OtherRepo> Others,
    IReadOnlyList<RepoInfo>? Repos = null)
{
    public const string MudBlazorRepo = "MudBlazor";
    public const string ExtensionsRepo = "MudBlazor.Extensions";

    /// <summary>True when the data says which items are tests (older snapshots do not).</summary>
    public bool KnowsTests => Hoods.Any(h => h.TestSize > 0);

    /// <summary>
    /// The same map with test code taken out: areas and rooms shrink by their test items, and the ones
    /// that were only tests disappear. Pull request counts stay as they are.
    /// </summary>
    public MapData WithoutTests()
    {
        var hoods = Hoods.Select(h => h with { Size = h.Size - h.TestSize, TestSize = 0 }).Where(h => h.Size > 0).ToList();
        var keys = hoods.Select(h => h.Key).ToHashSet();
        var clusters = Clusters.Where(c => keys.Contains(c.Hood))
            .Select(c => c with { N = c.N - c.TestN, TestN = 0 }).Where(c => c.N > 0).ToList();
        var roads = Roads.Where(r => keys.Contains(r.A) && keys.Contains(r.B)).ToList();
        var repos = Repos?.Select(r => r with { Nodes = r.Nodes - r.TestNodes, TestNodes = 0 }).ToList();
        return this with { Hoods = hoods, Clusters = clusters, Roads = roads, Repos = repos };
    }

    /// <summary>Areas of one repository, largest first.</summary>
    public IReadOnlyList<Hood> HoodsOf(string repo) =>
        Hoods.Where(h => h.Repo == repo).OrderByDescending(h => h.Size).ToList();

    /// <summary>Clusters of one area, largest first.</summary>
    public IReadOnlyList<Cluster> ClustersOf(string hoodKey) =>
        Clusters.Where(c => c.Hood == hoodKey).OrderByDescending(c => c.N).ToList();

    /// <summary>Heat level 0 (cold) to 6 (white hot) from a pull request count.</summary>
    public static int Heat(int prs) => prs switch
    {
        >= 30 => 6,
        >= 20 => 5,
        >= 10 => 4,
        >= 3 => 3,
        >= 1 => 2,
        _ => 0,
    };

    private static readonly HashSet<string> GenericNames =
    [
        "Task", "List", "IEnumerable", "IReadOnlyList", "Action", "Type", "Fact", "TestCase", "Test",
        "XElement", "CultureInfo", "CancellationToken", "RenderFragment", "IJSRuntime", "IRenderedComponent",
    ];

    /// <summary>A readable name for a cluster: its first specific type name.</summary>
    public static string RoomName(Cluster c)
    {
        if (!string.IsNullOrWhiteSpace(c.Name)) return c.Name;
        var pick = c.Top.FirstOrDefault(t => t.Length > 0 && char.IsUpper(t[0]) && !GenericNames.Contains(t))
            ?? c.Top.FirstOrDefault() ?? $"Cluster {c.Id}";
        return pick.TrimEnd('(', ')').Split('.')[^1];
    }
}
