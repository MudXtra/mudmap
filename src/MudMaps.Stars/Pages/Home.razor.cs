using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudMaps.Shared;
using MudMaps.Shared.Research;

namespace MudMaps.Stars.Pages;

/// <summary>
/// The Space map, one level at a time: the whole sky shows every repo as a whole star system (its star, planets, rings
/// and moons), each one target with one name; a star's system shows its planets (top-level areas), named; a planet
/// shows its satellites (the rooms of its code). Moving between levels is a ride: the camera glides towards what was
/// clicked while a ship runs a lane of buoys to a docking beacon that names the destination, and the next level
/// grows into view from the same spot.
/// </summary>
public partial class Home : MapPageBase, IAsyncDisposable
{
    private sealed record Orbit(double Cx, double Cy, double Rx, double K, double Angle, double Speed)
    {
        public double X => Cx + Math.Cos(Angle) * Rx;
        public double Y => Cy + Math.Sin(Angle) * Rx * K;
    }
    private sealed record Planet(Hood Hood, Orbit Orbit, double R, int Moons, bool Ring);
    private sealed record Dot(double X, double Y, double R, bool Twinkle, double Delay);
    private sealed record Arc(string Path);
    private sealed record Ring(double Cx, double Cy, double Rx, double Ry);

    /// <summary>
    /// One repo on the whole sky: its star with its areas as small planets on tilted rings. <see cref="Reach"/> is the
    /// half-width of the outer ring (the system's size), <see cref="PlateW"/> the room its name plate may take.
    /// </summary>
    private sealed record SkySystem(RepoInfo Repo, double X, double Y, double StarR, double Reach, double K, string Fill,
        IReadOnlyList<Ring> Rings, IReadOnlyList<Planet> Planets, double PlateY, double PlateW);

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private readonly List<SkySystem> _systems = [];
    private readonly List<Dot> _dots = [];
    private readonly List<Arc> _arcs = [];
    private readonly List<Planet> _planets = [];
    private readonly List<Ring> _rings = [];
    private readonly List<Ring> _satRings = [];
    private readonly Dictionary<string, Orbit> _satOrbits = [];

    private IJSObjectReference? _module;
    private DotNetObjectReference<Home>? _self;
    private bool _deepLinkHandled;
    private bool _restart;
    private string _conflictSig = "";
    private bool _busy;
    private bool _portrait;

    /// <summary>Screenshot hook (?ride=): milliseconds into a ride at which to freeze it; -1 plays rides normally.</summary>
    private double _freeze = -1;

    /// <summary>The star system in view (level 1, or the repo of the planet at level 2); null on the whole sky.</summary>
    private string? _repo;

    /// <summary>0 = whole sky, 1 = one star system, 2 = one planet with its satellites.</summary>
    private int Level => CurrentHood is not null ? 2 : _repo is not null ? 1 : 0;

    private RepoInfo? SystemRepo => _repo is null ? null : Data?.Repos?.FirstOrDefault(r => r.Name == _repo);

    /// <summary>Phones get a tall sky so names stay readable; wide screens a wide one.</summary>
    protected override double ViewWidth => _portrait ? 900 : 1600;
    protected override double ViewHeight => _portrait ? 1900 : 1000;

    /// <summary>Roughly how many chart units one screen pixel spans, so shapes keep a similar size on a phone.</summary>
    private double Px => _portrait ? 2.3 : 1.2;

    /// <summary>Name plates are at most this wide (screen pixels) and cut to fit inside.</summary>
    private int StarPlatePx => _portrait ? 170 : 210;
    private int PlanetPlatePx => _portrait ? 130 : 170;
    private int SatellitePlatePx => _portrait ? 130 : 180;

    protected override string RootLabel => "Whole sky";
    protected override (string Prs, string Merged, string Repos) FigureLabels => ("moons", "merged", "star systems");

    /// <summary>
    /// Every level fills the whole view, so the shared area zoom always frames the full chart (it never pans).
    /// With these radii <see cref="MapPageBase"/>'s zoom works out to exactly 0, 0, width, height.
    /// </summary>
    protected override (double X, double Y, double Rx, double Ry) Focus(Hood hood) => (ViewWidth / 2, ViewHeight / 2, ViewWidth / 3.4, 10);

    // ---- Lifecycle ------------------------------------------------------------------------------------------

    protected override async Task OnMapRenderedAsync()
    {
        // "./stars.js" goes through the import map, which names the fingerprinted file of this build, so a browser never
        // pairs this app with a cached script from an older one.
        _module = await JS.InvokeAsync<IJSObjectReference>("import", "./stars.js");
        _self = DotNetObjectReference.Create(this);
        var portrait = await _module.InvokeAsync<bool>("watchShape", Svg, _self);
        if (portrait != _portrait) Reshape(portrait);
        _restart = true;
        StateHasChanged();
    }

    protected override Task OnMapUpdatedAsync()
    {
        _restart = true;
        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>The page crossed the phone/wide boundary: lay every level out again for the new shape.</summary>
    [JSInvokable]
    public void ShapeChanged(bool portrait)
    {
        if (portrait == _portrait || Data is null) return;
        Reshape(portrait);
        _restart = true;
        StateHasChanged();
    }

    private void Reshape(bool portrait)
    {
        _portrait = portrait;
        if (Data is null) return;
        Build(Data);
        if (CurrentHood is { } hood) Rooms = LayoutRooms(hood, Focus(hood));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (Data is null || _module is null) return;
        // The figures follow the star system in view until the reader actually leaves it, sheet open or not.
        Scope.SetView(_repo);

        // An open area or repo sheet always belongs to the system in view. This is how ?zoom=<area> and ?repo=<name>
        // links land on their level (the base class opens them); in-map travel keeps it true by itself.
        if (!_busy && (CurrentHood?.Repo ?? CurrentRepo?.Name) is { } open && open != _repo)
        {
            _repo = open;
            BuildSystem();
            _restart = true;
            StateHasChanged();
            return;
        }

        if (!_deepLinkHandled)
        {
            _deepLinkHandled = true;
            var query = HttpUtility.ParseQueryString(new Uri(Nav.Uri).Query);
            if (query["tip"] is { Length: > 0 } tip) await _module.InvokeVoidAsync("peek", Svg, tip);
            // Screenshot hook: ?ride=<ms>&to=<repo> flies to that repo's system and freezes the ride that many ms in;
            // with ride-back=1 it starts in the system and flies back out to the whole sky.
            if (query["ride"] is { Length: > 0 } at && _systems.FirstOrDefault(s => s.Repo.Name == (query["to"] ?? MapData.MudBlazorRepo)) is { } sys)
            {
                _freeze = double.TryParse(at, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms : 1500;
                _ = FrozenRideAsync(sys.Repo, query["ride-back"] == "1");
            }
        }

        // The review board's conflict lines changed: stars.js picks up the new lines on the next start.
        var conflictSig = ConflictsShown && CurrentHood is { } open2 ? string.Join(",", ConflictRooms(open2.Repo).Select(c => c.RoomId)) : "";
        if (conflictSig != _conflictSig)
        {
            _conflictSig = conflictSig;
            _restart = true;
        }

        if (_restart)
        {
            _restart = false;
            await _module.InvokeVoidAsync("start", Svg);
        }
    }

    public async ValueTask DisposeAsync()
    {
        DetachResearch();
        Scope.SetView(null);
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        _self?.Dispose();
    }

    // ---- Layout ---------------------------------------------------------------------------------------------

    protected override void Build(MapData data)
    {
        _systems.Clear();
        _dots.Clear();
        _arcs.Clear();
        var w = ViewWidth;
        var h = ViewHeight;
        var rnd = new Random(11);
        var dotCount = (int)(320 * w * h / 1_600_000);
        for (var i = 0; i < dotCount; i++)
        {
            _dots.Add(new Dot(rnd.NextDouble() * w, rnd.NextDouble() * h, (rnd.NextDouble() < .08 ? 1.4 : .7) * Px / 1.2, i % 5 == 0, rnd.NextDouble() * 4));
        }

        LayoutSky(data);

        // The strongest pulls between systems, drawn star to star.
        var starOf = _systems.ToDictionary(s => s.Repo.Name);
        var repoOf = data.Hoods.ToDictionary(x => x.Key, x => x.Repo);
        var seen = new HashSet<(string, string)>();
        foreach (var road in data.Roads.OrderByDescending(x => x.W))
        {
            if (!repoOf.TryGetValue(road.A, out var a) || !repoOf.TryGetValue(road.B, out var b) || a == b) continue;
            if (!starOf.TryGetValue(a, out var sa) || !starOf.TryGetValue(b, out var sb)) continue;
            var pair = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
            if (!seen.Add(pair)) continue;
            var lift = Math.Min(140, Math.Abs(sa.X - sb.X) * .25 + 40);
            _arcs.Add(new Arc($"M{F(sa.X)},{F(sa.Y)} Q{F((sa.X + sb.X) / 2)},{F(Math.Min(sa.Y, sb.Y) - lift)} {F(sb.X)},{F(sb.Y)}"));
            if (_arcs.Count == 6) break;
        }

        BuildSystem();
    }

    /// <summary>Tilt of the rings on the whole sky (height over width).</summary>
    private const double SkyTilt = .44;

    /// <summary>
    /// The whole sky: every repo as a small star system, sized by its code (the outer ring grows with the cube root of
    /// its code items), packed biggest first around the middle of the view and then spread out to fill it. Each system
    /// is laid out in its own units first (rings and planets), then scaled to its size.
    /// </summary>
    private void LayoutSky(MapData data)
    {
        var w = ViewWidth;
        var h = ViewHeight;
        var repos = (data.Repos ?? []).OrderByDescending(r => r.Nodes).ToList();
        if (repos.Count == 0) return;
        var local = repos.Select(r => (Repo: r, Shape: LocalSystem(r, data.HoodsOf(r.Name)))).ToList();
        var want = local.Select(x => Math.Pow(Math.Max(1, x.Repo.Nodes), .3)).ToList();

        var margin = 8 * Px;
        var top = 14 * Px;
        var plateH = 44 * Px;
        var plateMin = (_portrait ? 150 : 170) * Px;
        var gap = 10 * Px;
        // Box each system takes: its rings, and its name plate hanging under them.
        (double W, double H) Box(int i, double c) => (Math.Max(2 * c * want[i] + gap, plateMin), 2 * c * want[i] * SkyTilt + 6 * Px + plateH + gap);

        // First guess: the boxes cover about half the view (solve 4c²a²k + 2ca·plate summed = area); shrink until every
        // one finds a place.
        var qa = want.Sum(a => 4 * a * a * SkyTilt);
        var qb = want.Sum(a => 2 * a * plateH);
        var c = (-qb + Math.Sqrt(qb * qb + 4 * qa * .5 * w * h)) / (2 * qa);
        var first = c;
        var step = 14 * Px / 1.2;
        var spots = new List<(double X, double Y)>();
        for (var y = top; y <= h - margin; y += step)
        {
            for (var x = margin; x <= w - margin; x += step) spots.Add((x, y));
        }
        // Middle first, a touch of noise so rows don't line up like a table.
        spots = spots.OrderBy(p => Math.Pow((p.X - w / 2) / w, 2) + 1.2 * Math.Pow((p.Y - h / 2) / h, 2) + Noise(p.X, p.Y) * .004).ToList();

        var placed = new List<(double X, double Y, double W, double H)>();
        for (var attempt = 0; attempt < 40; attempt++, c *= .94)
        {
            placed.Clear();
            foreach (var i in Enumerable.Range(0, local.Count))
            {
                var (bw, bh) = Box(i, c);
                foreach (var (x, y) in spots)
                {
                    var b = (X: x - bw / 2, Y: y - bh / 2, W: bw, H: bh);
                    if (b.X < margin || b.Y < top || b.X + b.W > w - margin || b.Y + b.H > h - margin) continue;
                    if (placed.Any(o => b.X < o.X + o.W && o.X < b.X + b.W && b.Y < o.Y + o.H && o.Y < b.Y + b.H)) continue;
                    placed.Add(b);
                    break;
                }
                if (placed.Count <= i) break;
            }
            if (placed.Count == local.Count) break;
        }
        if (placed.Count < local.Count || _portrait)
        {
            // Phones, and any sky the packing above can't fit: rows of systems, biggest first, spread evenly.
            c = first;
            for (var attempt = 0; attempt < 60 && !Rows(c); attempt++, c *= .94) { }
            if (placed.Count < local.Count) return;
        }

        bool Rows(double size)
        {
            placed.Clear();
            var rows = new List<List<int>>();
            var row = new List<int>();
            var rowW = 0.0;
            for (var i = 0; i < local.Count; i++)
            {
                var bw = Box(i, size).W;
                if (row.Count > 0 && rowW + bw > w - 2 * margin)
                {
                    rows.Add(row);
                    row = [];
                    rowW = 0;
                }
                row.Add(i);
                rowW += bw;
            }
            if (row.Count > 0) rows.Add(row);
            var heights = rows.Select(r => r.Max(i => Box(i, size).H)).ToList();
            var slackY = (h - margin - top - heights.Sum()) / (rows.Count + 1);
            if (slackY < 0) return false;
            var boxes = new (double X, double Y, double W, double H)[local.Count];
            var y = top + slackY;
            for (var j = 0; j < rows.Count; j++)
            {
                var slackX = (w - 2 * margin - rows[j].Sum(i => Box(i, size).W)) / (rows[j].Count + 1);
                var x = margin + slackX;
                foreach (var i in rows[j])
                {
                    var (bw, bh) = Box(i, size);
                    boxes[i] = (x, y + (heights[j] - bh) / 2, bw, bh);
                    x += bw + slackX;
                }
                y += heights[j] + slackY;
            }
            placed.AddRange(boxes);
            return true;
        }

        // Spread the packed boxes out from their middle as far as the view allows (moving centres apart never makes two
        // boxes overlap), then centre the whole sky in the view.
        var xs = Spread(placed.Select(b => (b.X + b.W / 2, b.W / 2)).ToList(), margin, w - margin);
        var ys = Spread(placed.Select(b => (b.Y + b.H / 2, b.H / 2)).ToList(), top, h - margin);

        for (var i = 0; i < local.Count; i++)
        {
            var b = placed[i];
            var bx = xs[i];
            var by = ys[i];
            var reach = c * want[i];
            var cy = by - b.H / 2 + gap / 2 + reach * SkyTilt;
            _systems.Add(Place(local[i].Repo, local[i].Shape, bx, cy, reach, c, b.W - gap, cy + reach * SkyTilt + 6 * Px));
        }

        // A name plate may use all the free room beside it, not only its own box: up to halfway to a neighbouring plate
        // at the same height, up to the rings of a system beside it, and up to the edge of the view.
        for (var i = 0; i < _systems.Count; i++)
        {
            var s = _systems[i];
            var half = Math.Min(s.X, w - s.X) - margin;
            foreach (var o in _systems.Where(o => o != s))
            {
                var dx = Math.Abs(o.X - s.X);
                if (Math.Abs(o.PlateY - s.PlateY) < plateH) half = Math.Min(half, dx / 2 - 3 * Px);
                if (o.Y + o.Reach * o.K > s.PlateY && o.Y - o.Reach * o.K < s.PlateY + plateH) half = Math.Min(half, dx - o.Reach - 3 * Px);
            }
            if (2 * half > s.PlateW) _systems[i] = s with { PlateW = 2 * half };
        }
    }

    /// <summary>Box centres on one axis moved apart from their middle as far as <paramref name="lo"/>..<paramref name="hi"/> allows, then centred in it.</summary>
    private static List<double> Spread(List<(double C, double Half)> boxes, double lo, double hi)
    {
        var mid = (boxes.Min(b => b.C - b.Half) + boxes.Max(b => b.C + b.Half)) / 2;
        var f = boxes.Select(b => Math.Abs(b.C - mid) < 1e-6 ? 3 : b.C < mid ? (mid - lo - b.Half) / (mid - b.C) : (hi - mid - b.Half) / (b.C - mid)).Prepend(3).Min();
        f = Math.Max(1, f);
        var moved = boxes.Select(b => mid + (b.C - mid) * f).ToList();
        var shift = (lo + hi) / 2 - (boxes.Select((b, i) => moved[i] - b.Half).Min() + boxes.Select((b, i) => moved[i] + b.Half).Max()) / 2;
        return moved.Select(m => m + shift).ToList();
    }

    private static double Noise(double x, double y)
    {
        var v = Math.Sin(x * 12.9898 + y * 78.233) * 43758.5453;
        return v - Math.Floor(v);
    }

    /// <summary>A star system in its own units (outer ring 1), centred on 0,0: one to four rings and a planet per area.</summary>
    private sealed record LocalShape(IReadOnlyList<double> Rings, IReadOnlyList<(Hood Hood, int Ring, double Angle)> Planets);

    private static LocalShape LocalSystem(RepoInfo repo, IReadOnlyList<Hood> hoods)
    {
        var count = hoods.Count <= 6 ? 1 : hoods.Count <= 14 ? 2 : hoods.Count <= 24 ? 3 : 4;
        var radii = Rings(hoods.Count, count == 1 ? .64 : .4, .9);
        var planets = new List<(Hood, int, double)>();
        var next = 0;
        for (var j = 0; j < radii.Count; j++)
        {
            var (_, n) = radii[j];
            for (var i = 0; i < n && next < hoods.Count; i++, next++)
            {
                planets.Add((hoods[next], j, (i + (j % 2) * .5) / n * Math.PI * 2 + j * .6 + repo.Name.Length * .37));
            }
        }
        return new LocalShape(radii.Select(x => x.R).ToList(), planets);
    }

    /// <summary>
    /// A local star system put on the sky at <paramref name="x"/>, <paramref name="y"/> with its outer ring
    /// <paramref name="reach"/> wide. The star grows with the system; planets are sized by their own code on the same
    /// scale (<paramref name="unit"/>) for every system, so a big area looks big wherever it is.
    /// </summary>
    private SkySystem Place(RepoInfo repo, LocalShape shape, double x, double y, double reach, double unit, double plateW, double plateY)
    {
        var rings = shape.Rings.Select(r => new Ring(x, y, r * reach, r * reach * SkyTilt)).ToList();
        var planets = shape.Planets.Select(p =>
        {
            var orbit = new Orbit(x, y, shape.Rings[p.Ring] * reach, SkyTilt, p.Angle, Math.PI * 2 / (70 + 25 * p.Ring));
            var r = Math.Max(unit * (.25 + Math.Sqrt(p.Hood.Size) * .018), 2.2 * Px);
            return new Planet(p.Hood, orbit, r, Math.Min(4, (int)Math.Ceiling(p.Hood.Prs / 6.0)), p.Hood.Prs >= 20);
        }).ToList();
        var fill = repo.Name == MapData.MudBlazorRepo ? "var(--gold)" : repo.Prs > 0 ? "var(--purple)" : "var(--silver)";
        return new SkySystem(repo, x, y, Math.Max(reach * .15, 4 * Px), reach, SkyTilt, fill, rings, planets, plateY, plateW);
    }

    /// <summary>Band across the top of a system or planet view where the docking plate hangs, clear of every orbit.</summary>
    private double PlateBand => 92 * Px;

    /// <summary>Room under the lowest orbit for its names.</summary>
    private double FootBand => 40 * Px;

    /// <summary>Centre of a star system or planet close-up, between the plate band and the foot band.</summary>
    private (double X, double Y) Centre => (ViewWidth / 2, (PlateBand + ViewHeight - FootBand) / 2);

    /// <summary>Largest orbit that still leaves room for names: wide on a desktop, tall on a phone.</summary>
    private (double Rx, double Ry) OrbitLimit => (ViewWidth / 2 - (_portrait ? 60 : 80) * Px, (ViewHeight - FootBand - PlateBand) / 2 - 14 * Px);

    /// <summary>The innermost orbit around a central body of radius <paramref name="body"/>: no body or name on it passes under the body.</summary>
    private double InnerOrbit(double body, double k) => Math.Max(body + 45 * Px, (body + 50 * Px) / k);

    private double SystemStarR => SystemRepo is { } repo ? Math.Min(Math.Min(ViewWidth, ViewHeight) * .085, (16 + Math.Sqrt(repo.Nodes) * .2) * Px) : 0;

    private double PlanetBodyR => Math.Min(ViewWidth, ViewHeight) * .1;

    /// <summary>The star system in view: its areas as planets on a few rings, largest on the inside.</summary>
    private void BuildSystem()
    {
        _planets.Clear();
        _rings.Clear();
        if (SystemRepo is not { } repo || Data is null) return;
        var hoods = Data.HoodsOf(repo.Name);
        var (cx, cy) = Centre;
        var (rxMax, ryMax) = OrbitLimit;
        var k = ryMax / rxMax;
        var r0 = InnerOrbit(SystemStarR, k);
        var rings = Rings(hoods.Count, r0, rxMax);
        var next = 0;
        for (var j = 0; j < rings.Count; j++)
        {
            var (rx, count) = rings[j];
            _rings.Add(new Ring(cx, cy, rx, rx * k));
            for (var i = 0; i < count && next < hoods.Count; i++, next++)
            {
                var hood = hoods[next];
                var angle = (i + (j % 2) * .5) / count * Math.PI * 2 + j * .35 - Math.PI / 2;
                var orbit = new Orbit(cx, cy, rx, k, angle, Math.PI * 2 / 360);
                var r = Math.Clamp((6 + Math.Sqrt(hood.Size) * .2) * Px, 7 * Px, 30 * Px);
                _planets.Add(new Planet(hood, orbit, r, Math.Min(8, (int)Math.Ceiling(hood.Prs / 4.0)), hood.Prs >= 20));
            }
        }
    }

    /// <summary>Ring radii from <paramref name="r0"/> to <paramref name="rMax"/>, with bodies shared out by ring length.</summary>
    private static List<(double R, int Count)> Rings(int n, double r0, double rMax)
    {
        if (n == 0) return [];
        var m = n <= 6 ? 1 : n <= 14 ? 2 : n <= 24 ? 3 : 4;
        var radii = Enumerable.Range(0, m).Select(j => m == 1 ? (r0 + rMax) / 2 : r0 + (rMax - r0) * j / (m - 1)).ToList();
        var total = radii.Sum();
        var counts = radii.Select(r => Math.Max(1, (int)Math.Round(n * r / total))).ToList();
        // Fix rounding on the outermost ring, which has the most room.
        counts[^1] += n - counts.Sum();
        while (counts[^1] < 1)
        {
            var big = counts.IndexOf(counts.Max());
            counts[big]--;
            counts[^1]++;
        }
        return radii.Zip(counts).ToList();
    }

    /// <summary>A planet's satellites (its rooms) on one or two rings around the planet.</summary>
    protected override List<MapRoom> LayoutRooms(Hood hood, (double X, double Y, double Rx, double Ry) f)
    {
        _satOrbits.Clear();
        _satRings.Clear();
        var clusters = Data!.ClustersOf(hood.Key);
        if (clusters.Count == 0) return [];
        var (cx, cy) = Centre;
        var (rxMax, ryMax) = OrbitLimit;
        var k = ryMax / rxMax;
        var r0 = InnerOrbit(PlanetBodyR, k);
        var inner = (int)Math.Ceiling(clusters.Count * .4);
        List<(double R, int Count)> rings = clusters.Count <= 7
            ? [((r0 + rxMax) / 2, clusters.Count)]
            : [(r0, inner), (rxMax, clusters.Count - inner)];
        var rooms = new List<MapRoom>();
        var next = 0;
        for (var j = 0; j < rings.Count; j++)
        {
            var (rx, count) = rings[j];
            _satRings.Add(new Ring(cx, cy, rx, rx * k));
            for (var i = 0; i < count && next < clusters.Count; i++, next++)
            {
                var cl = clusters[next];
                var angle = (i + (j % 2) * .5) / count * Math.PI * 2 - Math.PI / 2 + j * .2;
                var orbit = new Orbit(cx, cy, rx, k, angle, Math.PI * 2 / 420);
                _satOrbits[cl.Id] = orbit;
                var full = MapData.RoomName(cl);
                var r = Math.Clamp((8 + Math.Sqrt(cl.N) * .8) * Px, 9 * Px, 26 * Px);
                rooms.Add(new MapRoom(cl, SatelliteName(full), full, orbit.X, orbit.Y, r, 13));
            }
        }
        return rooms;
    }

    /// <summary>A satellite's own name: the part after "Area · ", without the Mud prefix every MudBlazor type shares.</summary>
    private static string SatelliteName(string full)
    {
        var name = full.Contains(" · ") ? full[(full.IndexOf(" · ", StringComparison.Ordinal) + 3)..] : full;
        return System.Text.RegularExpressions.Regex.Replace(name, "^(MudEx|MudX|Mud)(?=[A-Z])", "");
    }

    private Orbit SatOrbit(MapRoom room) => _satOrbits.TryGetValue(room.Cluster.Id, out var o) ? o : new Orbit(room.X, room.Y, 0, 1, 0, 0);

    // ---- Trail and text -------------------------------------------------------------------------------------

    /// <summary>Whole sky › star › planet › satellite. The star step names its repo, so the figures follow it.</summary>
    private IReadOnlyList<ZoomTrail.Step> SpaceTrail
    {
        get
        {
            var steps = new List<ZoomTrail.Step> { new(RootLabel, EventCallback.Factory.Create(this, HomeAsync)) };
            if (_repo is { } repo) steps.Add(new(repo, EventCallback.Factory.Create(this, UpToSystemAsync), repo));
            if (CurrentHood is { } hood) steps.Add(new(hood.Label, EventCallback.Factory.Create(this, CloseRoom), hood.Repo));
            if (CurrentRoom is { } room) steps.Add(new(MapData.RoomName(room), EventCallback.Factory.Create(this, () => { }), CurrentHood?.Repo));
            return steps;
        }
    }

    private string Lede => Level switch
    {
        2 => $"{CurrentHood!.Label}, a planet of {CurrentHood.Repo}. Each satellite is a room of its code.",
        1 => $"The {_repo} system. Each planet is a part of its code; click one to fly in.",
        _ => $"{Data?.Repos?.Count ?? 0} star systems, one for each repo. Click one to fly to it.",
    };

    private static string Prs(int n) => n == 1 ? "1 pull request" : $"{n.ToString(CultureInfo.InvariantCulture)} pull requests";

    private static string StarTip(RepoInfo repo) =>
        $"{repo.Prs.ToString(CultureInfo.InvariantCulture)} pull requests this month · {repo.Nodes.ToString("N0", CultureInfo.InvariantCulture)} code items";

    private static string HoodTip(Hood hood) =>
        $"{hood.Prs.ToString(CultureInfo.InvariantCulture)} pull requests this month · {hood.Size.ToString("N0", CultureInfo.InvariantCulture)} code items";

    // ---- Travel ---------------------------------------------------------------------------------------------

    /// <summary>
    /// How far the camera zooms on a star system on the whole sky before its own view takes over, and how big (as a
    /// share of its full size) the system view then starts: at that moment the clicked system and the new view line up,
    /// the system's outer ring on top of the new view's outer ring.
    /// </summary>
    private (double Zoom, double Start) SystemZoom(SkySystem sys) => Match(sys.Reach, OrbitLimit.Rx);

    /// <summary>The same for a planet in a system: its body lines up with the planet close-up's body.</summary>
    private (double Zoom, double Start) PlanetZoom(Planet p) => Match(p.R, PlanetBodyR);

    /// <summary>
    /// Zoom on a body of size <paramref name="small"/> that lines it up with the same body at size <paramref name="big"/>
    /// in the next view, which starts at about 60% of its full size. The zoom never drops below 2.4 and the start never
    /// passes 80%, so every ride visibly travels; where that breaks the line-up a little, the jump's flash covers it.
    /// </summary>
    private static (double Zoom, double Start) Match(double small, double big)
    {
        var zoom = Math.Clamp(.6 * big / Math.Max(1, small), 2.4, 14);
        return (zoom, Math.Clamp(zoom * small / big, .2, .8));
    }

    /// <summary>Camera target for a ride: a point (or an orbiting body, found live by its key), its size and shape, and the zoom on it.</summary>
    private static object Target(double x, double y, (double Zoom, double Start) z, double r, double k, string? key = null) =>
        new { x, y, zoom = z.Zoom, start = z.Start, r, k, key };

    /// <summary>
    /// A ride between levels. Going in (dir 1) the camera glides and zooms towards <paramref name="target"/> while the
    /// ship runs its buoy lane to a docking beacon naming the destination; the next level is drawn at the brightest
    /// moment of the jump and grows into view from the same spot. Going out (dir -1) the view shrinks away, the outer
    /// level appears zoomed in on where you were (<paramref name="target"/> again, now on the outer level) and glides
    /// back out while the ship runs home. Skip animations swaps at once with a short fade.
    /// </summary>
    private async Task RideAsync(int dir, string label, object? target, Func<Task> arrive, Action? landed = null)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (_module is null || SkipAnimations)
            {
                await arrive();
                _restart = true;
                StateHasChanged();
                if (_module is not null) await _module.InvokeVoidAsync("fade", Svg);
                landed?.Invoke();
                return;
            }
            // An open sheet closes before the ride rather than part way through it, so the stage keeps its size meanwhile.
            if (CurrentRepo is not null || CurrentRoom is not null)
            {
                CloseDetails();
                StateHasChanged();
                await Task.Yield();
            }
            var (lx, ly) = Centre;
            var opts = new { dir, kicker = dir > 0 ? "Docking at" : "Returning to", label, target, land = new { x = lx, y = ly } };
            await _module.InvokeAsync<double>("ride", Svg, opts, _freeze);
            await arrive();
            _restart = true;
            StateHasChanged();
            await _module.InvokeVoidAsync("surface", Svg, Level, _freeze);
            landed?.Invoke();
        }
        finally { _busy = false; }
    }

    /// <summary>A star on the whole sky: fly to its system, then open its sheet.</summary>
    private Task EnterSystemAsync(RepoInfo repo) => EnterSystemAsync(repo, true);

    /// <param name="openSheet">Open the repo's sheet on arrival (not while a research panel is open beside the map).</param>
    private Task EnterSystemAsync(RepoInfo repo, bool openSheet)
    {
        if (_systems.FirstOrDefault(s => s.Repo.Name == repo.Name) is not { } sys) return Task.CompletedTask;
        return RideAsync(1, repo.Name, Target(sys.X, sys.Y, SystemZoom(sys), sys.Reach, sys.K), () =>
        {
            _repo = repo.Name;
            CurrentHood = null;
            CurrentRoom = null;
            CurrentRepo = null;
            Rooms = [];
            BuildSystem();
            return Task.CompletedTask;
        }, () => { if (openSheet) OpenRepo(repo); StateHasChanged(); });
    }

    /// <summary>A planet in a system: fly in to its satellites.</summary>
    private Task EnterPlanetAsync(Hood hood)
    {
        if (_planets.FirstOrDefault(x => x.Hood.Key == hood.Key) is not { } p) return Task.CompletedTask;
        return RideAsync(1, hood.Label, Target(p.Orbit.X, p.Orbit.Y, PlanetZoom(p), p.R, 1, hood.Key), () =>
        {
            // The planet close-up always fills the view, so the shared area zoom has nothing to animate.
            CurrentHood = hood;
            CurrentRoom = null;
            CurrentRepo = null;
            Rooms = LayoutRooms(hood, Focus(hood));
            SetTrail();
            return Task.CompletedTask;
        });
    }

    /// <summary>From a planet back out to its star system.</summary>
    private Task UpToSystemAsync()
    {
        if (Level < 2 || _repo is not { } repo || CurrentHood is not { } hood) return Task.CompletedTask;
        if (_planets.FirstOrDefault(x => x.Hood.Key == hood.Key) is not { } p) return Task.CompletedTask;
        // The planet keeps its orbit position on the way out, so the system view comes back with it where it was.
        return RideAsync(-1, repo, Target(p.Orbit.X, p.Orbit.Y, PlanetZoom(p), p.R, 1, hood.Key), () =>
        {
            CurrentHood = null;
            CurrentRoom = null;
            Rooms = [];
            return Task.CompletedTask;
        });
    }

    /// <summary>Back to the whole sky from any level, in one ride.</summary>
    protected override Task HomeAsync()
    {
        if (Level == 0)
        {
            CloseDetails();
            return Task.CompletedTask;
        }
        var from = _systems.FirstOrDefault(s => s.Repo.Name == _repo);
        return RideAsync(-1, RootLabel, from is null ? null : Target(from.X, from.Y, SystemZoom(from), from.Reach, from.K), () =>
        {
            _repo = null;
            CurrentHood = null;
            CurrentRoom = null;
            CurrentRepo = null;
            Rooms = [];
            _planets.Clear();
            _rings.Clear();
            return Task.CompletedTask;
        });
    }

    /// <summary>Screenshot hook (?ride=): play one ride in or out of <paramref name="repo"/>, frozen part way.</summary>
    private async Task FrozenRideAsync(RepoInfo repo, bool back)
    {
        if (back)
        {
            _repo = repo.Name;
            BuildSystem();
            _restart = true;
            StateHasChanged();
            await Task.Delay(300);
            await HomeAsync();
        }
        else
        {
            await EnterSystemAsync(repo);
        }
    }

    // ---- Research: where "Show on map" goes --------------------------------------------------------------------

    protected override bool ResearchEnabled => true;

    /// <summary>The PRs colliding on a planet's satellites, "#a + #b", or null when none do.</summary>
    private string? PlanetConflict(Hood hood, IReadOnlyList<ConflictRoom> conflicts)
    {
        var ids = Data!.ClustersOf(hood.Key).Select(c => c.Id).ToHashSet();
        var numbers = conflicts.Where(c => ids.Contains(c.RoomId)).SelectMany(c => c.Numbers).Distinct().Order().ToList();
        return numbers.Count == 0 ? null : new ConflictRoom(hood.Key, numbers).Label;
    }

    /// <summary>Into a repo's star system: a ride from the whole sky, otherwise a short fade.</summary>
    protected override Task GoToRepoAsync(string repo)
    {
        if (Level == 1 && _repo == repo) return Task.CompletedTask;
        if (Data?.Repos?.FirstOrDefault(r => r.Name == repo) is not { } info) return Task.CompletedTask;
        if (Level == 0) return EnterSystemAsync(info, Research.Panel == ResearchPanelKind.None);
        return JumpAsync(() =>
        {
            _repo = repo;
            CurrentHood = null;
            CurrentRoom = null;
            CurrentRepo = null;
            Rooms = [];
            BuildSystem();
            SetTrail();
        });
    }

    /// <summary>To a planet: a ride from its own system, otherwise a short fade.</summary>
    protected override Task GoToHoodAsync(Hood hood)
    {
        if (CurrentHood?.Key == hood.Key) return Task.CompletedTask;
        if (Level == 1 && _repo == hood.Repo) return EnterPlanetAsync(hood);
        return JumpAsync(() =>
        {
            _repo = hood.Repo;
            BuildSystem();
            CurrentHood = hood;
            CurrentRoom = null;
            CurrentRepo = null;
            Rooms = LayoutRooms(hood, Focus(hood));
            SetTrail();
        });
    }

    protected override Task GoToWorldAsync() => Level == 0 ? Task.CompletedTask : HomeAsync();

    /// <summary>Change level at once with the short fade "Skip animations" uses.</summary>
    private async Task JumpAsync(Action arrive)
    {
        if (_busy) return;
        arrive();
        _restart = true;
        StateHasChanged();
        if (_module is not null) await _module.InvokeVoidAsync("fade", Svg);
    }

    /// <summary>Clicking empty sky goes up one level.</summary>
    private Task BackgroundAsync() => Level switch
    {
        2 => UpToSystemAsync(),
        1 => HomeAsync(),
        _ => Task.CompletedTask,
    };
}
