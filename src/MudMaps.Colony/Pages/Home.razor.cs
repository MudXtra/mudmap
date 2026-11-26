using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudMaps.Shared;

namespace MudMaps.Colony.Pages;

public partial class Home
{
    private const double Width = 1600;
    private const double Height = 1200;
    private const double Surface = 170;

    // Nest labels, in map units: at the usual desktop width (about 0.63 screen pixels per unit) names render at
    // 14 to 18px and counts at 12px.
    private const double NameSize = 22;
    private const double BigNameSize = 28;
    private const double CountSize = 19;
    private const double UpperRow = 52;

    // How much of a chamber the dug-in view shows: the chamber fills most of the stage so its rooms can carry readable names.
    private const double ZoomFactor = 2.4;

    // The three big nests have fixed homes; every other repo gets an even slot along the bottom band.
    private static readonly Dictionary<string, (double X, double Y)> BigNests = new()
    {
        ["MudBlazor"] = (470, 470),
        ["MudBlazor.Extensions"] = (1180, 400),
        ["CodeBeam Extensions"] = (1250, 760),
    };

    private static Dictionary<string, (double X, double Y)> NestCentres(IEnumerable<RepoInfo> repos)
    {
        var centres = new Dictionary<string, (double X, double Y)>(BigNests);
        var small = repos.Select(r => r.Name).Where(n => !centres.ContainsKey(n)).ToList();
        for (var i = 0; i < small.Count; i++)
        {
            var x = 90 + (Width - 180) * (small.Count == 1 ? .5 : i / (double)(small.Count - 1));
            centres[small[i]] = (x, i % 2 == 0 ? 1060 : 970);
        }
        return centres;
    }

    private sealed record Chamber(Hood Hood, double X, double Y, double Rx, double Ry);
    private sealed record Tunnel(string Path, double Width, int Heat, string A, string B);
    private sealed record Nest(RepoInfo Repo, double EntranceX, string Mound, bool Upper = false);
    private sealed record Room(Cluster Cluster, string Name, string FullName, double X, double Y, double R, double ViewWidth, double CellWidth);

    private MapData? _data;
    private MapData? _full;
    private bool _includeTests = true;
    private ElementReference _svg;
    private IJSObjectReference? _ants;
    private bool _antsStarted;
    private bool _layoutDirty = true;
    private bool _leaving;

    private IReadOnlyList<(string, string)> _figures = [];
    private IReadOnlyList<ZoomTrail.Step> _trail = [];
    private List<Chamber> _chambers = [];
    private readonly List<Tunnel> _tunnels = [];
    private readonly List<Nest> _nests = [];
    private List<Room> _rooms = [];
    private string _grassPath = "";

    private Hood? _hood;
    private Cluster? _room;
    private RepoInfo? _repo;

    private string LabelStyle => _hood is null ? "opacity:1" : "opacity:0";

    protected override async Task OnInitializedAsync()
    {
        Apply(await DataService.LoadLiveAsync());
        SetTrail();
    }

    private bool TestsKnown => _full?.KnowsTests ?? false;
    private int TestItems => _full?.Repos?.Sum(r => r.TestNodes) ?? 0;

    private void Apply(MapData data)
    {
        _full = data;
        _data = _includeTests || !data.KnowsTests ? data : data.WithoutTests();
        var repos = _data.Repos ?? [];
        _figures =
        [
            (repos.Sum(r => r.Prs).ToString(CultureInfo.InvariantCulture), "ants at work"),
            (repos.Sum(r => r.Merged).ToString(CultureInfo.InvariantCulture), "crumbs delivered"),
            (repos.Count.ToString(CultureInfo.InvariantCulture), "repos mapped"),
        ];
        BuildColony(_data);
        _layoutDirty = true;
    }

    /// <summary>A new map arrived from the service: redraw, keep the zoom, and re-seed the ants.</summary>
    private async Task RefreshAsync()
    {
        var data = await DataService.LoadLiveAsync();
        await InvokeAsync(() => RedrawAsync(data));
    }

    private async Task SetIncludeTestsAsync(bool include)
    {
        if (include == _includeTests || _full is null) return;
        _includeTests = include;
        await RedrawAsync(_full);
    }

    /// <summary>Rebuild the colony from new data, keep the open chamber if it still exists, and re-seed the ants.</summary>
    private async Task RedrawAsync(MapData data)
    {
        Apply(data);
        if (_hood is { } open)
        {
            if (_chambers.FirstOrDefault(c => c.Hood.Key == open.Key) is { } chamber)
            {
                _hood = chamber.Hood;
                _rooms = LayoutRooms(chamber);
                if (_room is { } room) _room = _data!.Clusters.FirstOrDefault(c => c.Id == room.Id);
            }
            else
            {
                _hood = null;
                _room = null;
                _rooms = [];
                await Zoom.ZoomToAsync(_svg, 0, 0, Width, Height);
            }
        }
        if (_repo is { } repo) _repo = _data!.Repos?.FirstOrDefault(r => r.FullName == repo.FullName);
        SetTrail();
        StateHasChanged();
        await Task.Yield();
        var repos = _data!.Repos ?? [];
        if (_ants is not null) await _ants.InvokeVoidAsync("start", _svg, repos.Sum(r => r.Prs), repos.Sum(r => r.Merged), Width);
    }

    private RepoInfo? RepoOf(Cluster? room)
    {
        if (room is null || _data is null) return null;
        var repo = _data.Hoods.FirstOrDefault(h => h.Key == room.Hood)?.Repo;
        return _data.Repos?.FirstOrDefault(r => r.Name == repo);
    }

    private void CloseDetails()
    {
        _repo = null;
        CloseRoom();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_data is null) return;
        if (!_antsStarted)
        {
            _antsStarted = true;
            var repos = _data.Repos ?? [];
            _ants = await JS.InvokeAsync<IJSObjectReference>("import", $"./colony.js?v={typeof(Home).Assembly.ManifestModule.ModuleVersionId:N}");
            await _ants.InvokeVoidAsync("start", _svg, repos.Sum(r => r.Prs), repos.Sum(r => r.Merged), Width);
            _layoutDirty = false;
            await _ants.InvokeVoidAsync("layout", _svg, Width, Height);

            // Deep link for sharing and screenshots: ?zoom=<area key>&room=<index>
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(Nav.Uri).Query);
            var demo = double.TryParse(query["ridedemo"], CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
            if (_chambers.FirstOrDefault(c => c.Hood.Key == query["zoom"]) is { } chamber)
            {
                if (demo is { } frozen)
                {
                    // Test hook: freeze the ant ride part way (?ridedemo=1 is the default frame; 0..1 picks one).
                    // With dir=up the chamber is opened first and the ride back to the surface is frozen instead.
                    var at = frozen is > 0 and < 1 ? frozen : .55;
                    if (query["dir"] == "up")
                    {
                        await OpenHoodAsync(chamber, animate: false);
                        await Task.Delay(300);
                        await GoHomeAsync(at);
                    }
                    else await OpenHoodAsync(chamber, animate: true, freeze: at);
                    return;
                }
                await OpenHoodAsync(chamber, animate: false);
                if (int.TryParse(query["room"], out var i) && i >= 0 && i < _rooms.Count) OpenRoom(_rooms[i]);
                StateHasChanged();
            }
            if (query["repo"] is { } repoName && (_data.Repos ?? []).FirstOrDefault(r => r.Name == repoName) is { } repo)
            {
                OpenRepo(repo);
                StateHasChanged();
            }

            // Live updates connect in the background; the colony is already usable.
            _ = DataService.WatchAsync(RefreshAsync);
            return;
        }

        if (_layoutDirty && _ants is not null)
        {
            _layoutDirty = false;
            await _ants.InvokeVoidAsync("layout", _svg, Width, Height);
        }
    }

    private void BuildColony(MapData data)
    {
        _tunnels.Clear();
        _nests.Clear();
        var rnd = new Random(5);

        var grass = new System.Text.StringBuilder($"M-600,{F(Surface)}");
        for (var x = -600; x <= 2200; x += 8) grass.Append($" L{x},{F(Surface - 4 - rnd.NextDouble() * 7)}");
        _grassPath = grass.Append($" L2200,{F(Surface + 3)} L-600,{F(Surface + 3)}Z").ToString();

        var centres = NestCentres(data.Repos ?? []);
        var hoods = data.Hoods.Where(h => centres.ContainsKey(h.Repo)).ToList();
        var nodes = hoods.Select(h => new LayoutNode(h.Key, h.Repo, 14 + Math.Sqrt(h.Size) * 0.66)).ToList();
        ForceLayout.Relax(nodes, centres, data.Roads, (16, Surface + 70, Width - 16, Height - 30), gap: 40, squash: 0.62, iterations: 700, pull: 0.008);
        var byKey = hoods.ToDictionary(h => h.Key);
        _chambers = nodes.Select(n => new Chamber(byKey[n.Key], n.X, n.Y, n.R, n.R * 0.62)).ToList();
        var chamberOf = _chambers.ToDictionary(c => c.Hood.Key);

        var roads = data.Roads.Where(r => chamberOf.ContainsKey(r.A) && chamberOf.ContainsKey(r.B)).Take(110).ToList();
        var maxW = roads.Max(r => r.W);
        foreach (var r in roads)
        {
            var a = chamberOf[r.A];
            var b = chamberOf[r.B];
            _tunnels.Add(new Tunnel(Curve(rnd, a.X, a.Y, b.X, b.Y), 4 + 10.0 * Math.Sqrt(r.W / (double)maxW), a.Hood.Prs + b.Hood.Prs, a.Hood.Key, b.Hood.Key));
        }

        foreach (var repo in data.Repos ?? [])
        {
            var top = _chambers.Where(c => c.Hood.Repo == repo.Name).MinBy(c => c.Y);
            if (top is null) continue;
            _nests.Add(new Nest(repo, top.X, Mound(top.X)));
        }
        SpreadNestLabels();

        // Each nest's tunnel starts at its (possibly nudged) entrance so the ride can use it.
        foreach (var n in _nests)
        {
            var top = _chambers.Where(c => c.Hood.Repo == n.Repo.Name).MinBy(c => c.Y)!;
            _tunnels.Add(new Tunnel(Curve(rnd, n.EntranceX, Surface - 2, top.X, top.Y), 10, top.Hood.Prs + n.Repo.Prs, NestId(n.Repo.Name), top.Hood.Key));
        }
    }

    private static string NestId(string repo) => $"nest:{repo}";

    private static string Mound(double x) => $"M{F(x - 34)},{F(Surface - 1)} Q{F(x)},{F(Surface - 30)} {F(x + 34)},{F(Surface - 1)}Z";

    // Nest names sit on the surface in two alternating rows; nudge entrances apart so names in the same row
    // do not collide, neighbouring mounds keep a little room, and an upper name's leader line clears the lower names.
    private void SpreadNestLabels()
    {
        var ordered = _nests.OrderBy(n => n.EntranceX).ToList();
        for (var pass = 0; pass < 60; pass++)
        {
            var moved = false;
            for (var i = 1; i < ordered.Count; i++)
            {
                var lower = ordered[i % 2 == 0 ? i : i - 1].Repo;
                moved |= Separate(ordered, i - 1, i, Math.Max(76, LabelWidth(lower) / 2 + 12));
                if (i >= 2) moved |= Separate(ordered, i - 2, i, (LabelWidth(ordered[i - 2].Repo) + LabelWidth(ordered[i].Repo)) / 2 + 24);
            }
            if (!moved) break;
        }
        _nests.Clear();
        _nests.AddRange(ordered.Select((n, i) => n with { Upper = i % 2 == 1 }));

        static bool Separate(List<Nest> list, int a, int b, double need)
        {
            var gap = list[b].EntranceX - list[a].EntranceX;
            if (gap >= need - .5) return false;
            var shift = (need - gap) / 2;
            list[a] = Move(list[a], -shift);
            list[b] = Move(list[b], shift);
            return true;
        }

        static Nest Move(Nest n, double dx)
        {
            var half = LabelWidth(n.Repo) / 2 + 8;
            var x = Math.Clamp(n.EntranceX + dx, half, Width - half);
            return n with { EntranceX = x, Mound = Mound(x) };
        }
    }

    private const int NestLabelMax = 16;
    private const int BigNestLabelMax = 22;

    private static bool IsBig(RepoInfo r) => r.Nodes > 10000;

    /// <summary>Rough rendered width of a nest's two label lines (Young Serif name, then the pull request count).</summary>
    private static double LabelWidth(RepoInfo r)
    {
        var name = IsBig(r)
            ? Math.Min(BigNestLabelMax, r.Name.Length) * BigNameSize * .6
            : Math.Min(NestLabelMax, r.Name.Length) * NameSize * .6;
        var count = (r.Prs > 0 ? $"{r.Prs} PRs".Length : "quiet".Length) * CountSize * .58;
        return Math.Max(name, count);
    }

    private static string Curve(Random rnd, double ax, double ay, double bx, double by)
    {
        var mx = (ax + bx) / 2 + (rnd.NextDouble() - .5) * 60;
        var my = (ay + by) / 2 + (rnd.NextDouble() - .5) * 50;
        return $"M{F(ax)},{F(ay)} Q{F(mx)},{F(my)} {F(bx)},{F(by)}";
    }

    /// <summary>Dig into a chamber: an ant ride along the tunnels, then the rooms appear where the zoom lands.</summary>
    private async Task OpenHoodAsync(Chamber c) => await OpenHoodAsync(c, animate: true);

    private async Task OpenHoodAsync(Chamber c, bool animate, double? freeze = null)
    {
        var w = c.Rx * ZoomFactor;
        var h = w * Height / Width;
        double[] view = [c.X - w / 2, c.Y - h / 2, w, h];
        var start = _hood?.Key;
        var already = _hood?.Key == c.Hood.Key && !_leaving;
        _leaving = false;
        _hood = c.Hood;
        _room = null;
        _repo = null;

        if (!animate || already || _ants is null)
        {
            _rooms = LayoutRooms(c);
            _layoutDirty = true;
            SetTrail();
            await Zoom.ZoomToAsync(_svg, view[0], view[1], view[2], view[3]);
            return;
        }

        _rooms = [];
        SetTrail();
        StateHasChanged();
        var sign = new { kicker = c.Hood.Repo, title = c.Hood.Label };
        var target = new { x = c.X, y = c.Y, rx = c.Rx, ry = c.Ry };
        var result = await _ants.InvokeAsync<string>("ride", _svg, new { start = start ?? NestId(c.Hood.Repo), end = c.Hood.Key, kind = "down", view, freeze, sign, target });
        if (result != "done" || _hood?.Key != c.Hood.Key) return;
        _rooms = LayoutRooms(c);
        _layoutDirty = true;
        StateHasChanged();
    }

    private List<Room> LayoutRooms(Chamber c)
    {
        var clusters = _data!.ClustersOf(c.Hood.Key);
        if (clusters.Count == 0) return [];
        var total = (double)clusters.Sum(x => x.N);
        // Cells about twice as wide as tall, so each room has a wide name line.
        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(clusters.Count * .95)));
        var rows = (int)Math.Ceiling(clusters.Count / (double)cols);
        var cw = c.Rx * 1.5 / cols;
        var ch = c.Ry * 1.3 / rows;
        return clusters.Select((cl, i) =>
        {
            var full = MapData.RoomName(cl);
            var shortName = full.Contains(" · ") ? full[(full.IndexOf(" · ") + 3)..] : full;
            shortName = System.Text.RegularExpressions.Regex.Replace(shortName, "^(MudEx|MudX|Mud)(?=[A-Z])", "");
            var name = Fit.Cut(shortName, 14);
            var cap = Math.Min(cw * .4, ch * .45 / .7);
            var r = Math.Min(cap, Math.Max(cap * .55, Math.Sqrt(cl.N / total * clusters.Count) * cap * .8));
            return new Room(cl, name, full, c.X - c.Rx * .75 + cw * (i % cols + .5), c.Y - c.Ry * .65 + ch * (i / cols + .5), r, c.Rx * ZoomFactor, cw);
        }).ToList();
    }

    private void OpenRoom(Room room)
    {
        _repo = null;
        _room = room.Cluster;
        SetTrail();
    }

    /// <summary>A nest click: the flying ant carries the rider down the nest's tunnel into its first chamber, then the repo opens.</summary>
    private async Task OpenNestAsync(Nest n)
    {
        var top = _chambers.Where(c => c.Hood.Repo == n.Repo.Name).MinBy(c => c.Y);
        if (top is null)
        {
            OpenRepo(n.Repo);
            return;
        }
        await OpenHoodAsync(top);
        if (_hood?.Key != top.Hood.Key || _leaving) return;
        OpenRepo(n.Repo);
        SetTrail();
    }

    private void OpenRepo(RepoInfo repo)
    {
        _room = null;
        _repo = repo;
    }

    private void CloseRoom()
    {
        _room = null;
        SetTrail();
    }

    private Task HomeAsync() => GoHomeAsync(null);

    /// <summary>Back to the whole colony: the ant carries the rider up to the surface while the view follows.</summary>
    private async Task GoHomeAsync(double? freeze)
    {
        _room = null;
        if (_hood is not { } open || _ants is null)
        {
            _hood = null;
            _rooms = [];
            SetTrail();
            await Zoom.ZoomToAsync(_svg, 0, 0, Width, Height);
            return;
        }

        _leaving = true;
        _rooms = [];
        SetTrail();
        StateHasChanged();
        double[] view = [0, 0, Width, Height];
        var sign = new { kicker = "Back on the surface", title = $"{open.Repo} nest" };
        var result = await _ants.InvokeAsync<string>("ride", _svg, new { start = open.Key, end = NestId(open.Repo), kind = "up", view, freeze, sign });
        if (result != "done" || !_leaving) return;
        _leaving = false;
        _hood = null;
        SetTrail();
        _layoutDirty = true;
        StateHasChanged();
    }

    /// <summary>A click on empty ground: climb back out when dug in, otherwise just close the sheet.</summary>
    private async Task BackgroundAsync()
    {
        if (_hood is null)
        {
            CloseDetails();
            return;
        }
        await HomeAsync();
    }

    private void SetTrail()
    {
        var steps = new List<ZoomTrail.Step> { new("Whole colony", EventCallback.Factory.Create(this, HomeAsync)) };
        if (_hood is { } hood && !_leaving)
        {
            var chamber = _chambers.First(c => c.Hood.Key == hood.Key);
            steps.Add(new($"{hood.Repo} › {hood.Label}", EventCallback.Factory.Create(this, () => OpenHoodAsync(chamber))));
        }
        if (_room is { } room)
        {
            steps.Add(new(MapData.RoomName(room), EventCallback.Factory.Create(this, () => { })));
        }
        _trail = steps;
    }

    private static async Task KeyAsync(KeyboardEventArgs e, Func<Task> action)
    {
        if (e.Key is "Enter" or " ") await action();
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        if (_ants is not null)
        {
            try { await _ants.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
