namespace MudMaps.Shared;

/// <summary>The totals for what is in view: one repository, or the whole map when <see cref="Repo"/> is null.</summary>
/// <param name="Repo">The repository the figures belong to, or null for every repository.</param>
/// <param name="Repos">How many repositories the figures cover.</param>
/// <param name="Areas">Areas on the map (test-only areas drop out when tests are hidden).</param>
/// <param name="CodeItems">Mapped code items, without tests when tests are hidden.</param>
/// <param name="TestItems">Test items, counted whether they are shown or hidden.</param>
public sealed record MapStats(string? Repo, int Repos, int Prs, int Merged, int Areas, int CodeItems, int TestItems)
{
    /// <summary>The figures for <paramref name="repo"/>, or for the whole map when it is null or not a mapped repository.</summary>
    /// <param name="full">The map with test code included.</param>
    public static MapStats For(MapData full, string? repo, bool includeTests)
    {
        var repos = (full.Repos ?? []).Where(r => repo is null || r.Name == repo).ToList();
        if (repo is not null && repos.Count == 0) return For(full, null, includeTests);
        var names = repos.Select(r => r.Name).ToHashSet();
        var areas = full.Hoods.Count(h => names.Contains(h.Repo) && (includeTests || h.Size > h.TestSize));
        var tests = repos.Sum(r => r.TestNodes);
        var items = repos.Sum(r => r.Nodes) - (includeTests ? 0 : tests);
        return new MapStats(repo, repos.Count, repos.Sum(r => r.Prs), repos.Sum(r => r.Merged), areas, items, tests);
    }
}

/// <summary>
/// Which repository the page is showing right now, so the figures above and below the map can follow it.
/// The shared components report it: the detail sheet (the open repo or room), the zoom trail (the zoomed area's repo),
/// and the map controls (whether tests are shown). A page can also say which repo is in view with <see cref="SetView"/>.
/// </summary>
public sealed class MapScope(MapDataService data)
{
    private string? _sheet;
    private string? _view;
    private string? _trail;

    /// <summary>Raised when the repository in scope or the test setting changes.</summary>
    public event Action? Changed;

    /// <summary>Whether tests count toward the figures, as set by the "Include tests" box.</summary>
    public bool IncludeTests { get; private set; } = true;

    /// <summary>Days of pull request history behind the figures.</summary>
    public int WindowDays => data.Current?.WindowDays ?? 30;

    /// <summary>The repository in scope: the open sheet's first, then the page's own view, then the zoomed area's.</summary>
    public string? Repo
    {
        get
        {
            var name = _sheet ?? _view ?? _trail;
            return name is not null && data.Current?.Repos?.Any(r => r.Name == name) == true ? name : null;
        }
    }

    /// <summary>Figures for the repository in scope, or the whole map; null before the data has loaded.</summary>
    public MapStats? Stats => data.Current is { } full ? MapStats.For(full, Repo, IncludeTests) : null;

    /// <summary>A repository from the full map (test counts intact), or null.</summary>
    public RepoInfo? RepoInfo(string? name) => name is null ? null : data.Current?.Repos?.FirstOrDefault(r => r.Name == name);

    /// <summary>The detail sheet shows this repository (or a room in it); null when the sheet is closed.</summary>
    public void SetSheet(string? repo) => Set(ref _sheet, repo);

    /// <summary>The page has zoomed onto this repository by some route the trail does not show; null when it leaves.</summary>
    public void SetView(string? repo) => Set(ref _view, repo);

    /// <summary>The zoom trail is inside this repository; null on the whole map.</summary>
    public void SetTrail(string? repo) => Set(ref _trail, repo);

    public void SetIncludeTests(bool include)
    {
        if (include == IncludeTests) return;
        IncludeTests = include;
        Changed?.Invoke();
    }

    private void Set(ref string? field, string? value)
    {
        if (field == value) return;
        var before = Repo;
        field = value;
        if (Repo != before) Changed?.Invoke();
    }
}
