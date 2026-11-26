using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudMaps.Shared.Research;

namespace MudMaps.Shared;

/// <summary>A cluster laid out as a clickable room inside a zoomed area.</summary>
public sealed record MapRoom(Cluster Cluster, string Name, string FullName, double X, double Y, double R, double FontSize);

/// <summary>
/// Zoom state shared by the map pages: whole map, one area with its rooms, one room's detail,
/// plus the repo card. A page supplies the drawing and where each area sits.
/// </summary>
public abstract class MapPageBase : ComponentBase
{
    [Inject] protected MapDataService DataService { get; set; } = default!;
    [Inject] protected ZoomService Zoom { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    /// <summary>Which repository is in view, for the figures; the shared sheet and trail keep it current.</summary>
    [Inject] protected MapScope Scope { get; set; } = default!;
    /// <summary>The reader's "Skip animations" choice; see <see cref="SkipAnimations"/>.</summary>
    [Inject] protected MotionSettings Motion { get; set; } = default!;
    /// <summary>Search lens, research drawer and review board state; used only when <see cref="ResearchEnabled"/>.</summary>
    [Inject] protected ResearchState Research { get; set; } = default!;

    protected MapData? Data;
    private MapData? _full;

    /// <summary>The map with test code included, whatever the toggle says; lay out from it so toggling never moves things.</summary>
    protected MapData? FullData => _full;

    /// <summary>Whether test code counts toward areas and rooms ("Include Tests" under the map).</summary>
    protected bool IncludeTests { get; private set; } = true;

    /// <summary>True when the data says which code is tests, so the toggle can work.</summary>
    protected bool TestsKnown => _full?.KnowsTests ?? false;

    /// <summary>How many mapped items are test code.</summary>
    protected int TestItems => _full?.Repos?.Sum(r => r.TestNodes) ?? 0;
    protected ElementReference Svg;
    protected Hood? CurrentHood;
    protected Cluster? CurrentRoom;
    protected RepoInfo? CurrentRepo;
    protected List<MapRoom> Rooms = [];
    protected IReadOnlyList<ZoomTrail.Step> Trail = [];
    protected IReadOnlyList<(string, string)> Figures = [];
    private bool _rendered;
    private bool _restoreView;

    /// <summary>
    /// True when the reader ticked "Skip animations" under the map: a level change should jump straight to its destination
    /// with a short fade (<see cref="ZoomService.FadeAsync"/>) instead of playing the map's ride. Read it before each ride.
    /// </summary>
    protected bool SkipAnimations => Motion.SkipAnimations;

    /// <summary>How long a zoom between levels animates, in ms. A map that travels under a cover (City's subway) uses 0.</summary>
    protected virtual int ZoomMs => 700;

    protected virtual double ViewWidth => 1600;
    protected virtual double ViewHeight => 1000;
    protected abstract string RootLabel { get; }
    protected virtual (string Prs, string Merged, string Repos) FigureLabels => ("pull requests", "merged", "repos mapped");

    /// <summary>Centre and radii of an area on the map, in SVG units.</summary>
    protected abstract (double X, double Y, double Rx, double Ry) Focus(Hood hood);

    /// <summary>Compute the map geometry from the data. Called again on every live update, so start from empty.</summary>
    protected abstract void Build(MapData data);

    /// <summary>Where an area is right now, for maps whose areas move.</summary>
    protected virtual Task<(double X, double Y)?> LiveCentreAsync(Hood hood) => Task.FromResult<(double X, double Y)?>(null);

    /// <summary>Runs once after the map is first drawn (start animations here).</summary>
    protected virtual Task OnMapRenderedAsync() => Task.CompletedTask;

    protected string LabelStyle => CurrentHood is null ? "opacity:1" : "opacity:0";

    protected string AreaStyle(Hood hood) => CurrentHood is null || CurrentHood.Key == hood.Key ? "opacity:1" : "opacity:.3";

    protected override async Task OnInitializedAsync()
    {
        await Motion.LoadAsync();
        Apply(await DataService.LoadLiveAsync());
        SetTrail();
        if (ResearchEnabled)
        {
            Research.Navigator = ShowLensAsync;
            Research.Changed += OnResearchChanged;
        }
    }

    private void Apply(MapData data)
    {
        _full = data;
        Data = IncludeTests || !data.KnowsTests ? data : data.WithoutTests();
        var repos = Data.Repos ?? [];
        Figures =
        [
            (repos.Sum(r => r.Prs).ToString(CultureInfo.InvariantCulture), FigureLabels.Prs),
            (repos.Sum(r => r.Merged).ToString(CultureInfo.InvariantCulture), FigureLabels.Merged),
            (repos.Count.ToString(CultureInfo.InvariantCulture), FigureLabels.Repos),
        ];
        Build(Data);
    }

    /// <summary>A new map arrived from the service: redraw in place and keep the current zoom.</summary>
    private async Task RefreshAsync()
    {
        var data = await DataService.LoadLiveAsync();
        await InvokeAsync(() => RedrawAsync(data));
    }

    /// <summary>Switch test code on or off and redraw, keeping the zoom where the area still exists.</summary>
    protected async Task SetIncludeTestsAsync(bool include)
    {
        if (include == IncludeTests || _full is null) return;
        IncludeTests = include;
        await RedrawAsync(_full);
    }

    private async Task RedrawAsync(MapData data)
    {
        var size = (ViewWidth, ViewHeight);
        Apply(data);
        // A new map size re-renders the viewBox to the whole map; put the zoom back once that render is done,
        // so the map keeps the same view (and the same screen size) after the toggle or a live update.
        if (size != (ViewWidth, ViewHeight)) _restoreView = true;
        if (CurrentHood is { } open)
        {
            if (Data!.Hoods.FirstOrDefault(h => h.Key == open.Key) is { } hood)
            {
                CurrentHood = hood;
                Rooms = LayoutRooms(hood, Focus(hood));
                if (CurrentRoom is { } room) CurrentRoom = Data.Clusters.FirstOrDefault(c => c.Id == room.Id);
            }
            else
            {
                // The area was only tests: back out a level once the redraw is done (the whole map, or the map's own view).
                CurrentHood = null;
                CurrentRoom = null;
                Rooms = [];
                _restoreView = true;
            }
        }
        if (CurrentRepo is { } repo) CurrentRepo = Data!.Repos?.FirstOrDefault(r => r.FullName == repo.FullName);
        SetTrail();
        StateHasChanged();
        await OnMapUpdatedAsync();
        // The open pull requests are refreshed with the map.
        if (ResearchEnabled) _ = Research.MapUpdatedAsync();
    }

    /// <summary>The repository a room belongs to, for the detail sheet's links.</summary>
    protected RepoInfo? RepoOf(Cluster? room)
    {
        if (room is null || Data is null) return null;
        var repo = Data.Hoods.FirstOrDefault(h => h.Key == room.Hood)?.Repo;
        return Data.Repos?.FirstOrDefault(r => r.Name == repo);
    }

    /// <summary>Close whichever detail is open in the sheet.</summary>
    protected void CloseDetails()
    {
        CurrentRepo = null;
        CloseRoom();
    }

    /// <summary>Runs after a live update has been drawn (refresh animations here).</summary>
    protected virtual Task OnMapUpdatedAsync() => Task.CompletedTask;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_restoreView && _rendered)
        {
            _restoreView = false;
            await RestoreViewAsync();
        }
        if (Data is null || _rendered) return;
        _rendered = true;
        await OnMapRenderedAsync();

        // Deep link for sharing and screenshots: ?zoom=<area key>&room=<index>
        var query = HttpUtility.ParseQueryString(new Uri(Nav.Uri).Query);
        if (Data.Hoods.FirstOrDefault(h => h.Key == query["zoom"]) is { } hood)
        {
            await OpenHoodAsync(hood);
            if (int.TryParse(query["room"], out var i) && i >= 0 && i < Rooms.Count) OpenRoom(Rooms[i]);
            StateHasChanged();
        }
        // Research links: ?repo=<name or owner/name>&room=<room id> lands on that room, selected and lit.
        if (ResearchEnabled && query["room"] is { } roomId && Data.Clusters.FirstOrDefault(c => c.Id == roomId) is { } linked
            && Data.Hoods.FirstOrDefault(h => h.Key == linked.Hood) is { } linkedHood
            && (query["repo"] is not { } want || want == linkedHood.Repo || want == Data.Repos?.FirstOrDefault(r => r.Name == linkedHood.Repo)?.FullName))
        {
            await OpenHoodAsync(linkedHood);
            if (Rooms.FirstOrDefault(r => r.Cluster.Id == roomId) is { } room) OpenRoom(room);
            Research.SetLens(MapData.RoomName(linked), [new LensRoom(Data.Repos?.FirstOrDefault(r => r.Name == linkedHood.Repo)?.FullName ?? linkedHood.Repo, roomId, 1)]);
            StateHasChanged();
        }
        else if (query["repo"] is { } repoName)
        {
            OpenRepo(Data.Repos?.FirstOrDefault(r => r.FullName == repoName)?.Name ?? repoName);
            StateHasChanged();
        }

        // Live updates connect in the background; the map is already usable.
        _ = DataService.WatchAsync(RefreshAsync);
    }

    protected async Task OpenHoodAsync(Hood hood)
    {
        var f = Focus(hood);
        if (await LiveCentreAsync(hood) is { } live) f = (live.X, live.Y, f.Rx, f.Ry);
        CurrentHood = hood;
        CurrentRoom = null;
        CurrentRepo = null;
        Rooms = LayoutRooms(hood, f);
        SetTrail();
        var v = HoodView(f);
        await Zoom.ZoomToAsync(Svg, v.X, v.Y, v.W, v.H, SkipAnimations ? 0 : ZoomMs);
    }

    /// <summary>The view box an area is zoomed to, from its focus.</summary>
    protected (double X, double Y, double W, double H) HoodView((double X, double Y, double Rx, double Ry) f)
    {
        var w = Math.Max(f.Rx, f.Ry / 0.62) * 3.4;
        var h = w * ViewHeight / ViewWidth;
        return (f.X - w / 2, f.Y - h / 2, w, h);
    }

    /// <summary>
    /// Puts the current view back at once after a redraw re-rendered the viewBox: the zoomed area, or the whole map.
    /// A map with views of its own (a district, a star system) overrides this.
    /// </summary>
    protected virtual async Task RestoreViewAsync()
    {
        if (CurrentHood is { } hood)
        {
            var f = Focus(hood);
            if (await LiveCentreAsync(hood) is { } live) f = (live.X, live.Y, f.Rx, f.Ry);
            var v = HoodView(f);
            await Zoom.ZoomToAsync(Svg, v.X, v.Y, v.W, v.H, 0);
        }
        else
        {
            await Zoom.ZoomToAsync(Svg, 0, 0, ViewWidth, ViewHeight, 0);
        }
    }

    protected virtual List<MapRoom> LayoutRooms(Hood hood, (double X, double Y, double Rx, double Ry) f)
    {
        var clusters = Data!.ClustersOf(hood.Key);
        if (clusters.Count == 0) return [];
        var total = (double)clusters.Sum(x => x.N);
        var cols = (int)Math.Ceiling(Math.Sqrt(clusters.Count * 1.7));
        var rows = (int)Math.Ceiling(clusters.Count / (double)cols);
        var cw = f.Rx * 1.5 / cols;
        var ch = f.Ry * 1.3 / rows;
        return clusters.Select((cl, i) =>
        {
            var full = MapData.RoomName(cl);
            var shortName = full.Contains(" · ") ? full[(full.IndexOf(" · ", StringComparison.Ordinal) + 3)..] : full;
            shortName = System.Text.RegularExpressions.Regex.Replace(shortName, "^(MudEx|MudX|Mud)(?=[A-Z])", "");
            var name = shortName.Length > 14 ? shortName[..13] + "…" : shortName;
            var r = Math.Min(cw * .46, Math.Max(cw * .24, Math.Sqrt(cl.N / total * clusters.Count) * cw * .36));
            var font = Math.Max(1.2, Math.Min(r * .34, r * 1.9 / (Math.Max(5, name.Length) * .62)));
            return new MapRoom(cl, name, full, f.X - f.Rx * .75 + cw * (i % cols + .5), f.Y - f.Ry * .65 + ch * (i / cols + .5), r, font);
        }).ToList();
    }

    protected void OpenRoom(MapRoom room)
    {
        CurrentRepo = null;
        CurrentRoom = room.Cluster;
        SetTrail();
    }

    protected void OpenRepo(RepoInfo repo)
    {
        CurrentRoom = null;
        CurrentRepo = repo;
    }

    protected void OpenRepo(string name)
    {
        if (Data?.Repos?.FirstOrDefault(r => r.Name == name) is { } repo) OpenRepo(repo);
    }

    protected void CloseRoom()
    {
        CurrentRoom = null;
        SetTrail();
    }

    protected void CloseRepo() => CurrentRepo = null;

    protected virtual async Task HomeAsync()
    {
        CurrentHood = null;
        CurrentRoom = null;
        Rooms = [];
        SetTrail();
        await Zoom.ZoomToAsync(Svg, 0, 0, ViewWidth, ViewHeight, SkipAnimations ? 0 : ZoomMs);
    }

    protected void SetTrail()
    {
        var steps = new List<ZoomTrail.Step> { new(RootLabel, EventCallback.Factory.Create(this, HomeAsync)) };
        if (CurrentHood is { } hood)
        {
            steps.Add(new($"{hood.Repo} › {hood.Label}", EventCallback.Factory.Create(this, () => OpenHoodAsync(hood)), hood.Repo));
        }
        if (CurrentRoom is { } room)
        {
            steps.Add(new(MapData.RoomName(room), EventCallback.Factory.Create(this, () => { }), CurrentHood?.Repo));
        }
        Trail = steps;
    }

    // ---- Research: search lens, review board, conflict lines ------------------------------------------------------

    /// <summary>True for maps that show the search lens, research drawer and review board (Space and City).</summary>
    protected virtual bool ResearchEnabled => false;

    private HashSet<string> _litRooms = [];
    private HashSet<string> _litHoods = [];
    private Dictionary<string, int> _litRepos = [];

    /// <summary>True while a lens or a hovered board card lights part of the map; everything else is dimmed.</summary>
    protected bool LensActive { get; private set; }

    /// <summary>The class for a room under the lens: lit, dimmed, or none when no lens is on.</summary>
    protected string LensClass(Cluster room) => !LensActive ? "" : _litRooms.Contains(room.Id) ? " mm-lit" : " mm-dim";

    protected string LensClass(Hood hood) => !LensActive ? "" : _litHoods.Contains(hood.Key) ? " mm-lit" : " mm-dim";

    /// <summary>The class for a repo (display name) on the whole map under the lens.</summary>
    protected string LensRepoClass(string repo) => !LensActive ? "" : _litRepos.ContainsKey(repo) ? " mm-lit" : " mm-dim";

    /// <summary>How many lit rooms a repo (display name) has, for its badge on the whole map.</summary>
    protected int LensCount(string repo) => _litRepos.GetValueOrDefault(repo);

    /// <summary>True while the review board is open: the map draws where open pull requests collide.</summary>
    protected bool ConflictsShown => ResearchEnabled && Research.BoardOpen;

    /// <summary>Rooms of a repo (display name) that two or more open PRs touch.</summary>
    protected IReadOnlyList<ConflictRoom> ConflictRooms(string repo) =>
        Research.FullName(repo) is { } full ? ConflictMap.Rooms(Research.Prs, full) : [];

    /// <summary>Conflict-risk PRs in a repo (display name), for its red badge on the whole map.</summary>
    protected int ConflictCount(string repo) =>
        Research.FullName(repo) is { } full ? ConflictMap.CountByRepo(Research.Prs).GetValueOrDefault(full) : 0;

    /// <summary>Each conflict-risk PR in a repo (display name) with the shared rooms its pin joins.</summary>
    protected IReadOnlyList<(int Number, IReadOnlyList<string> Rooms)> ConflictPins(string repo) =>
        Research.FullName(repo) is { } full && Research.FocusPr is { } f && f.Repo == full ? ConflictMap.Pins(Research.Prs, full, f.Number) : [];

    private ResearchPanelKind _panel;

    private void OnResearchChanged()
    {
        if (Data is null) return;
        // One panel at a time on the right: opening the research drawer or the board closes the detail sheet.
        if (Research.Panel != _panel)
        {
            _panel = Research.Panel;
            if (_panel != ResearchPanelKind.None) CloseDetails();
        }
        var hover = Research.Hover;
        var lens = Research.Lens;
        LensActive = hover.Count > 0 || lens is not null;
        _litRooms = hover.Count > 0 ? hover.ToHashSet() : Data.Clusters.Where(c => lens?.Lit(c) == true).Select(c => c.Id).ToHashSet();
        var hoods = Data.Clusters.Where(c => _litRooms.Contains(c.Id)).Select(c => c.Hood).ToList();
        _litHoods = hoods.ToHashSet();
        var repoOf = Data.Hoods.ToDictionary(h => h.Key, h => h.Repo);
        _litRepos = Data.Clusters.Where(c => _litRooms.Contains(c.Id) && repoOf.ContainsKey(c.Hood))
            .GroupBy(c => repoOf[c.Hood]).ToDictionary(g => g.Key, g => g.Count());
        if (ResearchEnabled && Research.BoardOpen) _ = Research.EnsurePrsAsync();
        InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Moves the map to what a lens lights: one room opens it, rooms in one area open the area, rooms in one repo open
    /// that repo, rooms across repos go out to the whole map where each repo shows its count.
    /// </summary>
    private async Task ShowLensAsync(MapLens lens)
    {
        if (Data is null) return;
        var rooms = Data.Clusters.Where(lens.Lit).ToList();
        if (rooms.Count == 0) return;
        var hoods = rooms.Select(r => r.Hood).Distinct().Select(k => Data.Hoods.First(h => h.Key == k)).ToList();
        var repos = hoods.Select(h => h.Repo).Distinct().ToList();
        if (rooms.Count == 1) await GoToRoomAsync(rooms[0], hoods[0]);
        else if (hoods.Count == 1) await GoToHoodAsync(hoods[0]);
        else if (repos.Count == 1) await GoToRepoAsync(repos[0]);
        else await GoToWorldAsync();
        StateHasChanged();
    }

    /// <summary>Open the room's area, then the room.</summary>
    protected virtual async Task GoToRoomAsync(Cluster room, Hood hood)
    {
        if (CurrentHood?.Key != hood.Key) await GoToHoodAsync(hood);
        // With a research panel open beside the map the lens marks the room; the detail sheet would crowd the panel.
        if (Research.Panel == ResearchPanelKind.None && Rooms.FirstOrDefault(r => r.Cluster.Id == room.Id) is { } open) OpenRoom(open);
    }

    protected virtual Task GoToHoodAsync(Hood hood) => CurrentHood?.Key == hood.Key ? Task.CompletedTask : OpenHoodAsync(hood);

    /// <summary>Show one repo (display name); a map with a repo level overrides this to travel there.</summary>
    protected virtual async Task GoToRepoAsync(string repo)
    {
        if (CurrentHood is not null) await HomeAsync();
        if (Research.Panel == ResearchPanelKind.None) OpenRepo(repo);
    }

    protected virtual Task GoToWorldAsync() => CurrentHood is null ? Task.CompletedTask : HomeAsync();

    /// <summary>Stop listening to the research state; call from the page's dispose.</summary>
    protected void DetachResearch()
    {
        if (!ResearchEnabled) return;
        Research.Changed -= OnResearchChanged;
        Research.Navigator = null;
    }

    protected static async Task KeyAsync(KeyboardEventArgs e, Func<Task> action)
    {
        if (e.Key is "Enter" or " ") await action();
    }

    protected static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
