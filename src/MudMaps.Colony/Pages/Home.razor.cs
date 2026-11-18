using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudMaps.Shared;

namespace MudMaps.Colony.Pages;

public partial class Home
{
    private const double Width = 1600;
    private const double Height = 1000;
    private const double Surface = 120;

    // Where each nest sits underground; the layout relaxes chambers around these.
    private static readonly Dictionary<string, (double X, double Y)> NestCentres = new()
    {
        ["MudBlazor"] = (470, 470),
        ["MudBlazor.Extensions"] = (1180, 400),
        ["CodeBeam Extensions"] = (1250, 780),
        ["MudX"] = (110, 860),
        ["Heron Calendar"] = (300, 880),
        ["Templates"] = (490, 900),
        ["Static Inputs"] = (680, 880),
        ["TryMudBlazor"] = (880, 860),
        ["ThemeManager"] = (1520, 900),
    };

    private sealed record Chamber(Hood Hood, double X, double Y, double Rx, double Ry);
    private sealed record Tunnel(string Path, double Width, int Heat);
    private sealed record Nest(RepoInfo Repo, double EntranceX, string Mound);
    private sealed record Room(Cluster Cluster, string Name, string FullName, double X, double Y, double R, double FontSize);

    private MapData? _data;
    private ElementReference _svg;
    private IJSObjectReference? _ants;
    private bool _antsStarted;

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

    private void Apply(MapData data)
    {
        _data = data;
        var repos = _data.Repos ?? [];
        _figures =
        [
            (repos.Sum(r => r.Prs).ToString(CultureInfo.InvariantCulture), "ants at work"),
            (repos.Sum(r => r.Merged).ToString(CultureInfo.InvariantCulture), "crumbs delivered"),
            (repos.Count.ToString(CultureInfo.InvariantCulture), "repos mapped"),
        ];
        BuildColony(_data);
    }

    /// <summary>A new map arrived from the service: redraw, keep the zoom, and re-seed the ants.</summary>
    private async Task RefreshAsync()
    {
        var data = await DataService.LoadLiveAsync();
        await InvokeAsync(async () =>
        {
            Apply(data);
            if (_hood is { } open && _chambers.FirstOrDefault(c => c.Hood.Key == open.Key) is { } chamber)
            {
                _hood = chamber.Hood;
                _rooms = LayoutRooms(chamber);
                if (_room is { } room) _room = data.Clusters.FirstOrDefault(c => c.Id == room.Id);
            }
            SetTrail();
            StateHasChanged();
            await Task.Yield();
            var repos = data.Repos ?? [];
            if (_ants is not null) await _ants.InvokeVoidAsync("start", _svg, repos.Sum(r => r.Prs), repos.Sum(r => r.Merged), Width);
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_data is null || _antsStarted) return;
        _antsStarted = true;
        var repos = _data.Repos ?? [];
        _ants = await JS.InvokeAsync<IJSObjectReference>("import", $"./colony.js?v={typeof(Home).Assembly.ManifestModule.ModuleVersionId:N}");
        await _ants.InvokeVoidAsync("start", _svg, repos.Sum(r => r.Prs), repos.Sum(r => r.Merged), Width);

        // Deep link for sharing and screenshots: ?zoom=<area key>&room=<index>
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(Nav.Uri).Query);
        if (_chambers.FirstOrDefault(c => c.Hood.Key == query["zoom"]) is { } chamber)
        {
            await OpenHoodAsync(chamber);
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
    }

    private void BuildColony(MapData data)
    {
        _tunnels.Clear();
        _nests.Clear();
        var rnd = new Random(5);

        var grass = new System.Text.StringBuilder($"M-600,{F(Surface)}");
        for (var x = -600; x <= 2200; x += 8) grass.Append($" L{x},{F(Surface - 4 - rnd.NextDouble() * 7)}");
        _grassPath = grass.Append($" L2200,{F(Surface + 3)} L-600,{F(Surface + 3)}Z").ToString();

        var hoods = data.Hoods.Where(h => NestCentres.ContainsKey(h.Repo)).ToList();
        var nodes = hoods.Select(h => new LayoutNode(h.Key, h.Repo, 16 + Math.Sqrt(h.Size) * 0.72)).ToList();
        ForceLayout.Relax(nodes, NestCentres, data.Roads, (16, Surface + 70, Width - 16, Height - 30), gap: 40, squash: 0.62, iterations: 700, pull: 0.008);
        var byKey = hoods.ToDictionary(h => h.Key);
        _chambers = nodes.Select(n => new Chamber(byKey[n.Key], n.X, n.Y, n.R, n.R * 0.62)).ToList();
        var chamberOf = _chambers.ToDictionary(c => c.Hood.Key);

        var roads = data.Roads.Where(r => chamberOf.ContainsKey(r.A) && chamberOf.ContainsKey(r.B)).Take(110).ToList();
        var maxW = roads.Max(r => r.W);
        foreach (var r in roads)
        {
            var a = chamberOf[r.A];
            var b = chamberOf[r.B];
            _tunnels.Add(new Tunnel(Curve(rnd, a.X, a.Y, b.X, b.Y), 4 + 10.0 * Math.Sqrt(r.W / (double)maxW), a.Hood.Prs + b.Hood.Prs));
        }

        foreach (var repo in data.Repos ?? [])
        {
            var top = _chambers.Where(c => c.Hood.Repo == repo.Name).MinBy(c => c.Y);
            if (top is null) continue;
            var x = top.X;
            _tunnels.Add(new Tunnel(Curve(rnd, x, Surface - 2, top.X, top.Y), 10, top.Hood.Prs + repo.Prs));
            _nests.Add(new Nest(repo, x, $"M{F(x - 34)},{F(Surface - 1)} Q{F(x)},{F(Surface - 30)} {F(x + 34)},{F(Surface - 1)}Z"));
        }
        SpreadNestLabels();
    }

    // Nest names sit in one row on the surface; nudge entrances apart so names do not collide.
    private void SpreadNestLabels()
    {
        var ordered = _nests.OrderBy(n => n.EntranceX).ToList();
        for (var pass = 0; pass < 20; pass++)
        {
            for (var i = 1; i < ordered.Count; i++)
            {
                var need = (Width1(ordered[i - 1].Repo) + Width1(ordered[i].Repo)) / 2 + 24;
                var gap = ordered[i].EntranceX - ordered[i - 1].EntranceX;
                if (gap >= need) continue;
                var shift = (need - gap) / 2;
                ordered[i - 1] = Move(ordered[i - 1], -shift);
                ordered[i] = Move(ordered[i], shift);
            }
        }
        _nests.Clear();
        _nests.AddRange(ordered);

        static double Width1(RepoInfo r) => Math.Max(110, r.Name.Length * (r.Nodes > 10000 ? 13.5 : 9));

        static Nest Move(Nest n, double dx)
        {
            var x = Math.Clamp(n.EntranceX + dx, 70, Width - 70);
            return n with { EntranceX = x, Mound = $"M{F(x - 34)},{F(Surface - 1)} Q{F(x)},{F(Surface - 30)} {F(x + 34)},{F(Surface - 1)}Z" };
        }
    }

    private static string Curve(Random rnd, double ax, double ay, double bx, double by)
    {
        var mx = (ax + bx) / 2 + (rnd.NextDouble() - .5) * 60;
        var my = (ay + by) / 2 + (rnd.NextDouble() - .5) * 50;
        return $"M{F(ax)},{F(ay)} Q{F(mx)},{F(my)} {F(bx)},{F(by)}";
    }

    private async Task OpenHoodAsync(Chamber c)
    {
        _hood = c.Hood;
        _room = null;
        _repo = null;
        _rooms = LayoutRooms(c);
        SetTrail();
        var w = c.Rx * 3.4;
        var h = w * Height / Width;
        await Zoom.ZoomToAsync(_svg, c.X - w / 2, c.Y - h / 2, w, h);
    }

    private List<Room> LayoutRooms(Chamber c)
    {
        var clusters = _data!.ClustersOf(c.Hood.Key);
        if (clusters.Count == 0) return [];
        var total = (double)clusters.Sum(x => x.N);
        var cols = (int)Math.Ceiling(Math.Sqrt(clusters.Count * 1.7));
        var rows = (int)Math.Ceiling(clusters.Count / (double)cols);
        var cw = c.Rx * 1.5 / cols;
        var ch = c.Ry * 1.3 / rows;
        return clusters.Select((cl, i) =>
        {
            var full = MapData.RoomName(cl);
            var shortName = full.Contains(" · ") ? full[(full.IndexOf(" · ") + 3)..] : full;
            shortName = System.Text.RegularExpressions.Regex.Replace(shortName, "^(MudEx|MudX|Mud)(?=[A-Z])", "");
            var name = shortName.Length > 14 ? shortName[..13] + "…" : shortName;
            var r = Math.Min(cw * .46, Math.Max(cw * .24, Math.Sqrt(cl.N / total * clusters.Count) * cw * .36));
            var font = Math.Max(1.2, Math.Min(r * .34, r * 2.3 / (Math.Max(5, name.Length) * .58)));
            return new Room(cl, name, full, c.X - c.Rx * .75 + cw * (i % cols + .5), c.Y - c.Ry * .65 + ch * (i / cols + .5), r, font);
        }).ToList();
    }

    private void OpenRoom(Room room)
    {
        _repo = null;
        _room = room.Cluster;
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

    private async Task HomeAsync()
    {
        _hood = null;
        _room = null;
        _rooms = [];
        SetTrail();
        await Zoom.ZoomToAsync(_svg, 0, 0, Width, Height);
    }

    private void SetTrail()
    {
        var steps = new List<ZoomTrail.Step> { new("Whole colony", EventCallback.Factory.Create(this, HomeAsync)) };
        if (_hood is { } hood)
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
