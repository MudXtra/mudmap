using System.Text;
using System.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudMaps.Shared;

namespace MudMaps.Islands.Pages;

public partial class Home : MapPageBase, IAsyncDisposable
{
    private const double Squash = 0.8;

    /// <summary>The home port drawn beside the compass rose; voyages start and end at its quay.</summary>
    private static readonly (double X, double Y) HarbourMark = (1372, 104);
    private static readonly (double X, double Y) Harbour = (HarbourMark.X, HarbourMark.Y + 30);

    /// <summary>
    /// Where the ?saildemo=1 shot stops: late in the voyage, with the island close, the signpost up on its quay
    /// and the repo name on the sail. ?saildemo=0.4 (any fraction) stops elsewhere; ?saildemo=home sails back out.
    /// </summary>
    private const double SailFreeze = 0.8;

    private static readonly Dictionary<string, (double X, double Y)> Waters = new()
    {
        ["MudBlazor"] = (380, 520),
        ["MudBlazor.Extensions"] = (960, 470),
        ["UltimateAuth"] = (1420, 360),
        ["CodeBeam Extensions"] = (1360, 760),
        ["MudX"] = (120, 1000),
        ["TryMudBlazor"] = (370, 1010),
        ["Heron Calendar"] = (640, 1000),
        ["Templates"] = (890, 1010),
        ["Static Inputs"] = (1120, 1000),
        ["ThemeManager"] = (1420, 1050),
        ["MudEx RichTextEditor"] = (170, 1300),
        ["ThemeCreator"] = (490, 1300),
        ["CodeAnalysis"] = (800, 1300),
        ["LottiePlayer"] = (1100, 1300),
        ["Icons"] = (1400, 1310),
    };

    protected override double ViewHeight => 1450;

    /// <summary>Rows of swell drawn in the channels between the island bands.</summary>
    private static readonly double[] WaveRows = [860, 905, 1150, 1195, 1420];

    /// <summary>Each repo's home water; a repo not charted yet gets a slot in the southern band.</summary>
    private Dictionary<string, (double X, double Y)> WatersFor(MapData data)
    {
        var waters = new Dictionary<string, (double X, double Y)>(Waters);
        var unplaced = (data.Repos ?? []).Select(r => r.Name).Where(n => !waters.ContainsKey(n)).ToList();
        for (var i = 0; i < unplaced.Count; i++)
        {
            waters[unplaced[i]] = (160 + (ViewWidth - 320) * (unplaced.Count == 1 ? .5 : i / (double)(unplaced.Count - 1)), ViewHeight - 60);
        }
        return waters;
    }

    private sealed record Flag(double X, double Y);
    private sealed record Box(double X, double Y, double W, double H)
    {
        public double Overlap(Box o) =>
            Math.Max(0, Math.Min(X + W, o.X + o.W) - Math.Max(X, o.X)) * Math.Max(0, Math.Min(Y + H, o.Y + o.H) - Math.Max(Y, o.Y));
    }

    /// <summary>An island's name tag: where its text starts, its baseline, and an optional leader line back to the coast.</summary>
    private sealed record Tag(string Name, string? Count, double X, double Baseline, Box Box, string? Leader);
    private sealed record Island(Hood Hood, double X, double Y, double R, string Shore, string Coast, List<string> Rings, List<Flag> Flags)
    {
        public Tag? Tag { get; set; }
    }
    private sealed record Route(string Path, double Width, bool Cross);
    private sealed record GroupLabel(string Repo, double X, double Y, int Prs);

    // Island names: upright serif in ink, the flag count in dark red italic. Sizes are SVG units; the chart is
    // drawn about 0.73 px per unit on a desktop, so 18 units is about 13 px on screen and 16.5 units about 12 px.
    private const double NameSize = 18;
    private const double CountSize = 16.5;
    private const double RepoSize = 21;

    /// <summary>Estimated text widths for the label placer (Newsreader semibold and italic).</summary>
    private static double NameWidth(string s) => s.Sum(GlyphWidth) * NameSize;
    private static double CountWidth(string s) => s.Length * CountSize * .43;

    /// <summary>Rough advance widths (in ems) for Newsreader semibold, measured against the rendered chart.</summary>
    private static double GlyphWidth(char c) => c switch
    {
        'i' or 'l' or 'j' or 't' or 'f' or 'r' or '.' or ',' or '\'' or ' ' or 'I' => .3,
        'm' or 'w' => .8,
        'M' or 'W' => .9,
        '&' or '…' => .75,
        _ when char.IsUpper(c) => .66,
        _ => .52,
    };

    private static string? CountText(Hood h) => h.Prs > 0 ? $"{h.Prs} {(h.Prs == 1 ? "flag" : "flags")}" : null;

    private readonly List<Island> _islands = [];
    private readonly List<Route> _routes = [];
    private readonly List<GroupLabel> _groups = [];
    private readonly Dictionary<string, Island> _byKey = [];

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private IJSObjectReference? _module;
    private bool _sailing;
    private bool _sailDemoHandled;
    private bool _deepLinkChecked;

    protected override string RootLabel => "Whole chart";
    protected override (string Prs, string Merged, string Repos) FigureLabels => ("flags raised", "made port", "islands surveyed");

    protected override (double X, double Y, double Rx, double Ry) Focus(Hood hood)
    {
        var i = _byKey[hood.Key];
        var rx = Math.Max(i.R * 1.4, 40);
        return (i.X, i.Y, rx, rx * .62);
    }

    protected override async Task OnMapRenderedAsync()
    {
        _module = await JS.InvokeAsync<IJSObjectReference>("import", "./island.js");
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (Data is not null && _module is not null && CurrentHood is not null && Rooms.Count > 0) await _module.InvokeVoidAsync("fitVillages", Svg);
        if (Data is null || _module is null || _sailDemoHandled) return;
        if (!_deepLinkChecked)
        {
            // Opened straight onto an island (?zoom=): the ship is already alongside its quay.
            _deepLinkChecked = true;
            if (CurrentHood is { } landed) await _module.InvokeVoidAsync("moor", Svg, Voyage(null, landed));
        }
        var query = HttpUtility.ParseQueryString(new Uri(Nav.Uri).Query);
        if (query["saildemo"] is not { Length: > 0 } demo) return;
        _sailDemoHandled = true;
        // Freeze part way through a voyage so a screenshot catches the ship under sail; nothing lands.
        var hood = Data.Hoods.FirstOrDefault(h => h.Key == (query["to"] ?? "mudblazor/datagrid") && _byKey.ContainsKey(h.Key)) ?? _islands.FirstOrDefault()?.Hood;
        if (hood is null) return;
        if (demo == "home")
        {
            await OpenHoodAsync(hood);
            StateHasChanged();
            await Task.Delay(900);
            CurrentHood = null;
            Rooms = [];
            SetTrail();
            StateHasChanged();
            await _module.InvokeAsync<double>("sail", Svg, HomeVoyage(hood, SailFreeze));
            return;
        }
        var freeze = double.TryParse(demo, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) && f is > 0 and < 1 ? f : SailFreeze;
        await _module.InvokeAsync<double>("sail", Svg, Voyage(null, hood, freeze));
    }

    /// <summary>Where a ship moors at an island: a quay just off its southern coast, clear of the villages.</summary>
    private (double X, double Y) Shore(Hood hood)
    {
        if (!_byKey.TryGetValue(hood.Key, out var i)) return Harbour;
        var f = Focus(hood);
        return (i.X, i.Y + Math.Max(i.R * Squash * 1.15, f.Ry * 1.1));
    }

    /// <summary>The view an island is zoomed to; the same box <see cref="MapPageBase.OpenHoodAsync"/> lands on.</summary>
    private (double X, double Y, double W, double H) ViewOf(Hood hood)
    {
        var f = Focus(hood);
        var w = Math.Max(f.Rx, f.Ry / 0.62) * 3.4;
        var h = w * ViewHeight / ViewWidth;
        return (f.X - w / 2, f.Y - h / 2, w, h);
    }

    /// <summary>The sailing part of a voyage: 2.4 to 2.8 seconds, ease-in-out; the module adds a half-second arrival beat.</summary>
    private static int SailMs((double X, double Y) from, (double X, double Y) to) =>
        (int)Math.Clamp(2400 + Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2)) * .3, 2400, 2800);

    /// <summary>A voyage to an island, from the island we are on or from the home port.</summary>
    private object Voyage(Hood? here, Hood hood, double freeze = -1)
    {
        var from = here is null ? Harbour : Shore(here);
        var to = Shore(hood);
        var view = ViewOf(hood);
        var i = _byKey[hood.Key];
        return new
        {
            fx = from.X, fy = from.Y, tx = to.X, ty = to.Y,
            coast = i.Y + i.R * Squash * .8,
            isle = new[] { i.X, i.Y, i.R * 1.3, i.R * 1.3 * Squash },
            dest = hood.Key,
            view = new[] { view.X, view.Y, view.W, view.H },
            ms = SailMs(from, to), freeze,
            name = hood.Repo,
            sign = hood.Label,
        };
    }

    /// <summary>The voyage back out to the home port, the same length as the way in.</summary>
    private object HomeVoyage(Hood here, double freeze = -1)
    {
        var from = Shore(here);
        return new
        {
            fx = from.X, fy = from.Y, tx = Harbour.X, ty = Harbour.Y,
            coast = HarbourMark.Y + 14,
            isle = Array.Empty<double>(),
            dest = "",
            view = new[] { 0, 0, ViewWidth, ViewHeight },
            ms = SailMs(from, Harbour), freeze,
            name = here.Repo,
            sign = "Home port",
        };
    }

    /// <summary>Click an island: sail there from the home port (or from the island we are on), then land.</summary>
    private async Task LandAsync(Hood hood)
    {
        if (_sailing) return;
        if (_module is null || CurrentHood?.Key == hood.Key)
        {
            await OpenHoodAsync(hood);
            return;
        }
        _sailing = true;
        try
        {
            var voyage = Voyage(CurrentHood, hood);
            if (CurrentHood is not null)
            {
                // Leaving one island for another: its villages go before the ship casts off.
                CurrentRoom = null;
                CurrentRepo = null;
                Rooms = [];
                StateHasChanged();
            }
            await _module.InvokeAsync<double>("sail", Svg, voyage);
            await OpenHoodAsync(hood);
        }
        finally
        {
            _sailing = false;
        }
    }

    /// <summary>Back to the whole chart: the villages go, then the ship sails back out to the home port.</summary>
    protected override async Task HomeAsync()
    {
        if (_module is null || _sailing || CurrentHood is not { } here)
        {
            if (_module is not null) await _module.InvokeVoidAsync("unmoor");
            await base.HomeAsync();
            return;
        }
        _sailing = true;
        try
        {
            CurrentHood = null;
            CurrentRoom = null;
            Rooms = [];
            SetTrail();
            StateHasChanged();
            await _module.InvokeAsync<double>("sail", Svg, HomeVoyage(here));
            await base.HomeAsync();
        }
        finally
        {
            _sailing = false;
        }
    }

    /// <summary>The distance between neighbouring villages in a row: a village name may use that much width.</summary>
    private double VillagePitch =>
        Rooms.GroupBy(r => Math.Round(r.Y, 1)).Select(row => row.OrderBy(r => r.X).ToList())
            .SelectMany(row => row.Zip(row.Skip(1), (a, b) => b.X - a.X)).DefaultIfEmpty(0).Min();

    /// <summary>A village's own name: the part after "Area · ", without the Mud prefix every MudBlazor type shares.</summary>
    private static string VillageName(string full)
    {
        var name = full.Contains(" · ") ? full[(full.IndexOf(" · ", StringComparison.Ordinal) + 3)..] : full;
        return System.Text.RegularExpressions.Regex.Replace(name, "^(MudEx|MudX|Mud)(?=[A-Z])", "");
    }

    private Task BackgroundAsync() => CurrentHood is null ? Task.CompletedTask : HomeAsync();

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }

    protected override void Build(MapData data)
    {
        _islands.Clear();
        _routes.Clear();
        _groups.Clear();
        _byKey.Clear();
        var waters = WatersFor(data);
        var hoods = data.Hoods.Where(h => waters.ContainsKey(h.Repo)).ToList();
        var radius = hoods.ToDictionary(h => h.Key, h => 10 + Math.Sqrt(h.Size) * 0.55);
        // Each island takes the room its name needs too, so the names have open water to sit in.
        var nodes = hoods.Select(h => new LayoutNode(h.Key, h.Repo, Math.Max(radius[h.Key] * 1.15, TagWidth(h) * .4) + 6)).ToList();
        ForceLayout.Relax(nodes, waters, data.Roads, (20, 190, 1580, ViewHeight - 30), gap: 20, squash: Squash, iterations: 900, pull: 0.03);
        var byKey = hoods.ToDictionary(h => h.Key);

        var seed = 1;
        foreach (var n in nodes)
        {
            var h = byKey[n.Key];
            var r = radius[h.Key];
            seed++;
            var rings = new List<string>();
            var count = Math.Min(5, 1 + h.Size / 500);
            for (var k = 1; k <= count; k++) rings.Add(Blob(n.X + 2 * k, n.Y - 2 * k, r, seed, 1 - k * .17));
            var flags = new List<Flag>();
            var nf = Math.Min(8, (int)Math.Ceiling(h.Prs / 4.0));
            for (var f = 0; f < nf; f++)
            {
                var a = f / (double)Math.Max(1, nf) * Math.PI * 2 + .4;
                flags.Add(new Flag(n.X + Math.Cos(a) * r * .55, n.Y + Math.Sin(a) * r * .42));
            }
            var island = new Island(h, n.X, n.Y, r, Blob(n.X, n.Y, r, seed, 1.25), Blob(n.X, n.Y, r, seed, 1), rings, flags);
            _islands.Add(island);
            _byKey[h.Key] = island;
        }

        var roads = data.Roads.Where(r => _byKey.ContainsKey(r.A) && _byKey.ContainsKey(r.B)).Take(100).ToList();
        var maxW = roads.Count > 0 ? roads.Max(r => r.W) : 1;
        foreach (var r in roads)
        {
            var a = _byKey[r.A];
            var b = _byKey[r.B];
            var mx = (a.X + b.X) / 2;
            var my = (a.Y + b.Y) / 2 - 30;
            _routes.Add(new Route($"M{F(a.X)},{F(a.Y)} Q{F(mx)},{F(my)} {F(b.X)},{F(b.Y)}", 1 + 2.5 * Math.Sqrt(r.W / (double)maxW), a.Hood.Repo != b.Hood.Repo));
        }

        PlaceLabels(data);
    }

    private static double TagWidth(Hood h) =>
        NameWidth(Fit.Cut(h.Label, 16)) + (CountText(h) is { } c ? 6 + CountWidth(c) : 0);

    /// <summary>The rough footprint of an island on the chart, for the label placer.</summary>
    private static Box Footprint(Island i) => new(i.X - i.R * 1.2, i.Y - i.R * 1.2 * Squash, i.R * 2.4, i.R * 2.4 * Squash);

    /// <summary>
    /// Sea names first, then island names, biggest islands first. Each name tries spots around its island (below,
    /// above, beside, then further out with a leader line) and takes the one that covers the fewest names and islands.
    /// </summary>
    private void PlaceLabels(MapData data)
    {
        var islands = _islands.Select(Footprint).ToList();
        var placed = new List<Box>();
        var harbour = new Box(HarbourMark.X - 70, HarbourMark.Y - 40, 140, 104);
        placed.Add(harbour);
        placed.Add(new Box(1470, 50, 100, 100)); // compass rose

        double Cost(Box b, int own)
        {
            var c = 0.0;
            foreach (var p in placed) c += p.Overlap(b) * 6;
            for (var k = 0; k < islands.Count; k++) c += islands[k].Overlap(b) * (k == own ? 1.5 : 2.5);
            var inside = new Box(8, 150, ViewWidth - 16, ViewHeight - 158);
            c += (b.W * b.H - inside.Overlap(b)) * 8;
            return c;
        }

        foreach (var repo in data.Repos ?? [])
        {
            var group = _islands.Where(i => i.Hood.Repo == repo.Name).ToList();
            if (group.Count == 0) continue;
            var cx = group.Average(i => i.X);
            var top = group.Min(i => i.Y - i.R * Squash * 1.3);
            var w = Fit.Cut(repo.Name, 24).Length * RepoSize * .43;
            Box? best = null;
            var bestCost = double.MaxValue;
            for (var step = 0; step < 8; step++)
            {
                foreach (var dx in new[] { 0.0, -.35, .35 })
                {
                    var b = new Box(cx - w / 2 + dx * w, top - 26 - step * 12, w, 24);
                    var c = Cost(b, -1) + step * 20 + Math.Abs(dx) * 60;
                    if (c < bestCost) { bestCost = c; best = b; }
                }
            }
            placed.Add(best!);
            _groups.Add(new GroupLabel(repo.Name, best!.X + w / 2, best.Y + 19, repo.Prs));
        }

        const double h = 21;
        foreach (var (island, index) in _islands.Select((i, k) => (i, k)).OrderByDescending(p => p.i.R))
        {
            var name = Fit.Cut(island.Hood.Label, 16);
            var count = CountText(island.Hood);
            var w = TagWidth(island.Hood);
            var rx = island.R * 1.2;
            var ry = island.R * 1.2 * Squash;
            var (x, y) = (island.X, island.Y);
            var spots = new List<(Box Box, double Penalty)>
            {
                (new(x - w / 2, y + ry + 1, w, h), 0),
                (new(x - w / 2, y - ry - h - 1, w, h), 8),
                (new(x + rx + 3, y - h / 2, w, h), 12),
                (new(x - rx - 3 - w, y - h / 2, w, h), 12),
                (new(x + rx * .6, y + ry * .7, w, h), 16),
                (new(x - rx * .6 - w, y + ry * .7, w, h), 16),
                (new(x + rx * .6, y - ry * .7 - h, w, h), 20),
                (new(x - rx * .6 - w, y - ry * .7 - h, w, h), 20),
            };
            for (var step = 1; step <= 4; step++)
            {
                var d = step * 14;
                spots.Add((new(x - w / 2, y + ry + 1 + d, w, h), 30 + step * 25));
                spots.Add((new(x - w / 2, y - ry - h - 1 - d, w, h), 34 + step * 25));
                spots.Add((new(x + rx + 3 + d, y - h / 2, w, h), 38 + step * 25));
                spots.Add((new(x - rx - 3 - w - d, y - h / 2, w, h), 38 + step * 25));
            }
            var (box, _) = spots.MinBy(s => Cost(s.Box, index) + s.Penalty);
            placed.Add(box);
            // A name pushed off its coast gets a thin leader line back to the island.
            var bx = Math.Clamp(x, box.X, box.X + box.W);
            var by = Math.Clamp(y, box.Y, box.Y + box.H);
            var gap = Math.Sqrt(Math.Pow((bx - x) / rx, 2) + Math.Pow((by - y) / ry, 2));
            string? leader = null;
            if (gap > 1.35)
            {
                var len = Math.Sqrt(Math.Pow(bx - x, 2) + Math.Pow(by - y, 2));
                var ex = x + (bx - x) / len * rx * .85;
                var ey = y + (by - y) / len * ry * .85;
                leader = $"M{F(ex)},{F(ey)} L{F(bx)},{F(by)}";
            }
            island.Tag = new Tag(name, count, box.X, box.Y + h * .76, box, leader);
        }
    }

    // A wobbly island outline, deterministic per seed.
    private static string Blob(double cx, double cy, double r, int seed, double k)
    {
        var rnd = new Random(seed);
        var ph = new[] { rnd.NextDouble() * 6, rnd.NextDouble() * 6, rnd.NextDouble() * 6 };
        const int n = 28;
        var pts = new (double X, double Y)[n];
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)n * Math.PI * 2;
            var rr = r * k * (1 + .16 * Math.Sin(3 * t + ph[0]) + .1 * Math.Sin(5 * t + ph[1]) + .06 * Math.Sin(7 * t + ph[2]));
            pts[i] = (cx + Math.Cos(t) * rr, cy + Math.Sin(t) * rr * Squash);
        }
        var sb = new StringBuilder($"M{F(pts[0].X)},{F(pts[0].Y)}");
        for (var i = 0; i < n; i++)
        {
            var p0 = pts[i];
            var p1 = pts[(i + 1) % n];
            sb.Append($" Q{F(p0.X)},{F(p0.Y)} {F((p0.X + p1.X) / 2)},{F((p0.Y + p1.Y) / 2)}");
        }
        return sb.Append('Z').ToString();
    }
}
