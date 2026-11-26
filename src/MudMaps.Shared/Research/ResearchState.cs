namespace MudMaps.Shared.Research;

/// <summary>Which research panel is open beside the map.</summary>
public enum ResearchPanelKind { None, Research, Board }

/// <summary>
/// Research state shared by the search bar, the research drawer, the review board and the map page: the lens on the
/// map, the card the reader hovers, which panel is open, the open pull requests and the graph figures per repo.
/// </summary>
public sealed class ResearchState : IDisposable
{
    private readonly MapDataService _data;
    private readonly MapScope _scope;
    private readonly ResearchClient _client;
    private readonly Dictionary<string, Task<GraphFigures?>> _figures = [];
    private Task? _prsLoad;

    public ResearchState(MapDataService data, MapScope scope, ResearchClient client)
    {
        _data = data;
        _scope = scope;
        _client = client;
        _scope.Changed += OnScopeChanged;
    }

    public ResearchClient Client => _client;

    /// <summary>Raised when anything here changes; components re-render.</summary>
    public event Action? Changed;

    /// <summary>Raised when another component asks the research drawer to open a tab (and maybe pick a PR).</summary>
    public event Action<string, PrItem?>? TabRequested;

    public MapData? Data => _data.Current;

    /// <summary>Configured repos as (display name, full name), in map order.</summary>
    public IReadOnlyList<(string Name, string FullName)> Repos =>
        (Data?.Repos ?? []).Select(r => (r.Name, r.FullName)).ToList();

    public string? FullName(string? name) => name is null ? null : Data?.Repos?.FirstOrDefault(r => r.Name == name)?.FullName;
    public string? NameOf(string? fullName) => fullName is null ? null : Data?.Repos?.FirstOrDefault(r => r.FullName == fullName)?.Name;

    /// <summary>The search bar's scope; it follows the repo the map is in.</summary>
    public SearchScope Search { get; } = new();

    /// <summary>The repo the map is zoomed into (full name), or null on the whole map.</summary>
    public string? Zoomed => Search.Zoomed;

    // ---- Lens ------------------------------------------------------------------------------------------------------

    public MapLens? Lens { get; private set; }

    public void SetLens(string label, IEnumerable<LensRoom> rooms)
    {
        if (Data is not { } data) return;
        Lens = new MapLens(label, rooms, data);
        Changed?.Invoke();
    }

    public void ClearLens()
    {
        if (Lens is null) return;
        Lens = null;
        Changed?.Invoke();
    }

    /// <summary>The map page moves the view to what a lens shows; set by the page.</summary>
    public Func<MapLens, Task>? Navigator { get; set; }

    /// <summary>Light <paramref name="rooms"/> and move the map to them.</summary>
    public async Task ShowOnMapAsync(string label, IEnumerable<LensRoom> rooms)
    {
        SetLens(label, rooms);
        if (Lens is { } lens && Navigator is { } go) await go(lens);
    }

    // ---- Hover (board cards) ---------------------------------------------------------------------------------------

    private static readonly HashSet<string> None = [];

    /// <summary>Rooms of the card under the pointer, lit on the map.</summary>
    public IReadOnlySet<string> Hover { get; private set; } = None;

    public void SetHover(IEnumerable<string>? rooms)
    {
        var next = rooms is null ? None : rooms.ToHashSet();
        if (next.SetEquals(Hover)) return;
        Hover = next;
        Changed?.Invoke();
    }

    /// <summary>
    /// The PR whose card is hovered or open on the board. Only its conflict lines are drawn: on a busy repo every PR's
    /// lines at once (MudBlazor has over a hundred) cannot be read.
    /// </summary>
    public (string Repo, int Number)? FocusPr { get; private set; }

    public void SetFocus(PrItem? pr)
    {
        (string, int)? next = pr is null ? null : (pr.Repo, pr.Number);
        if (next == FocusPr) return;
        FocusPr = next;
        Changed?.Invoke();
    }

    // ---- Panels ----------------------------------------------------------------------------------------------------

    public ResearchPanelKind Panel { get; private set; }

    public bool BoardOpen => Panel == ResearchPanelKind.Board;

    public void Open(ResearchPanelKind panel)
    {
        if (Panel == panel) return;
        Panel = panel;
        if (panel != ResearchPanelKind.Board) { SetHover(null); SetFocus(null); }
        Changed?.Invoke();
    }

    public void Toggle(ResearchPanelKind panel) => Open(Panel == panel ? ResearchPanelKind.None : panel);

    /// <summary>Open the research drawer on <paramref name="tab"/>, with <paramref name="pr"/> picked for PR impact.</summary>
    public void OpenTab(string tab, PrItem? pr = null)
    {
        Open(ResearchPanelKind.Research);
        TabRequested?.Invoke(tab, pr);
    }

    // ---- Open pull requests ----------------------------------------------------------------------------------------

    /// <summary>Every open pull request, for autocomplete; loaded once and again with each map update.</summary>
    public IReadOnlyList<PrItem> Prs { get; private set; } = [];

    public string? PrsError { get; private set; }

    public Task EnsurePrsAsync() => _prsLoad ??= LoadPrsAsync();

    /// <summary>The map has new data: reload the open pull requests and forget the cached graph figures.</summary>
    public Task MapUpdatedAsync()
    {
        _figures.Clear();
        _prsLoad = LoadPrsAsync();
        return _prsLoad;
    }

    private async Task LoadPrsAsync()
    {
        var result = await _client.PrsAsync([], actionable: false);
        if (result.Ok)
        {
            Prs = result.Value!.Prs ?? [];
            PrsError = null;
        }
        else
        {
            PrsError = result.Error;
        }
        Changed?.Invoke();
    }

    public PrItem? Pr(string repo, int number) => Prs.FirstOrDefault(p => p.Repo == repo && p.Number == number);

    // ---- Graph figures ---------------------------------------------------------------------------------------------

    /// <summary>graph_stats for one repo (full name), or the merged graph when null; once per repo per session.</summary>
    public Task<GraphFigures?> FiguresAsync(string? fullName)
    {
        var key = fullName ?? "";
        if (!_figures.TryGetValue(key, out var task)) _figures[key] = task = LoadFiguresAsync(fullName);
        return task;
    }

    private async Task<GraphFigures?> LoadFiguresAsync(string? fullName)
    {
        var args = new Dictionary<string, object?>();
        if (fullName is not null) args["project_path"] = fullName;
        var result = await _client.CallAsync("graph_stats", args);
        if (result.Ok && !result.Value!.IsError) return GraphFigures.Parse(result.Value.Text);
        // Ask again next time instead of caching a failure.
        _figures.Remove(fullName ?? "");
        return null;
    }

    private void OnScopeChanged()
    {
        var before = Search.Zoomed;
        Search.Follow(FullName(_scope.Repo));
        if (Search.Zoomed != before) Changed?.Invoke();
    }

    public void Dispose() => _scope.Changed -= OnScopeChanged;
}
