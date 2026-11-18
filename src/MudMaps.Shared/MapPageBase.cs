using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

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

    protected MapData? Data;
    private MapData? _full;

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

    protected virtual double ViewWidth => 1600;
    protected virtual double ViewHeight => 1000;
    protected abstract string RootLabel { get; }
    protected virtual (string Prs, string Merged, string Repos) FigureLabels => ("pull requests", "merged", "repos mapped");

    /// <summary>Centre and radii of an area on the map, in SVG units.</summary>
    protected abstract (double X, double Y, double Rx, double Ry) Focus(Hood hood);

    /// <summary>Compute the map geometry once the data is loaded.</summary>
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
        Apply(await DataService.LoadLiveAsync());
        SetTrail();
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
        Apply(data);
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
                // The area was only tests: back out to the whole map.
                CurrentHood = null;
                CurrentRoom = null;
                Rooms = [];
                await Zoom.ZoomToAsync(Svg, 0, 0, ViewWidth, ViewHeight);
            }
        }
        if (CurrentRepo is { } repo) CurrentRepo = Data!.Repos?.FirstOrDefault(r => r.FullName == repo.FullName);
        SetTrail();
        StateHasChanged();
        await OnMapUpdatedAsync();
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
        if (query["repo"] is { } repoName)
        {
            OpenRepo(repoName);
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
        var w = Math.Max(f.Rx, f.Ry / 0.62) * 3.4;
        var h = w * ViewHeight / ViewWidth;
        await Zoom.ZoomToAsync(Svg, f.X - w / 2, f.Y - h / 2, w, h);
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
            var font = Math.Max(1.2, Math.Min(r * .34, r * 2.3 / (Math.Max(5, name.Length) * .58)));
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
        await Zoom.ZoomToAsync(Svg, 0, 0, ViewWidth, ViewHeight);
    }

    private void SetTrail()
    {
        var steps = new List<ZoomTrail.Step> { new(RootLabel, EventCallback.Factory.Create(this, HomeAsync)) };
        if (CurrentHood is { } hood)
        {
            steps.Add(new($"{hood.Repo} › {hood.Label}", EventCallback.Factory.Create(this, () => OpenHoodAsync(hood))));
        }
        if (CurrentRoom is { } room)
        {
            steps.Add(new(MapData.RoomName(room), EventCallback.Factory.Create(this, () => { })));
        }
        Trail = steps;
    }

    protected static async Task KeyAsync(KeyboardEventArgs e, Func<Task> action)
    {
        if (e.Key is "Enter" or " ") await action();
    }

    protected static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
