using System.Globalization;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudMaps.Shared;
using MudMaps.Shared.Research;

namespace MudMaps.City.Pages;

/// <summary>
/// The Mud repositories as a world of hex tiles, one city per repository, one level at a time. The world map shows each
/// city whole (walls, districts, farms, its border and a banner with its name and pull requests) as one target.
/// Travelling into a city shows its districts (the repo's areas) by name; travelling into a district opens its rooms.
/// Every move between levels is a journey: a caravan walks the road while the camera follows it to the destination,
/// and a banner unfurls with the name of the place on arrival.
/// </summary>
public partial class Home : MapPageBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private IJSObjectReference? _module;

    // ---- World geometry ---------------------------------------------------------------------------------------
    // Pointy-top hexes in axial coordinates (q, r). The world is 1600 by 1000 map units; the tiles run a few hexes past
    // every edge (open sea) so a camera near the edge never shows a gap.

    private const double Hr = 21;                              // hex radius
    private static readonly double Hw = Math.Sqrt(3) * Hr;     // hex width
    private const double WorldW = 1600, WorldH = 1000;
    private const int Bleed = 4;

    // Type sizes in map units. The world is 1600 units across a stage of about 1170 px, so a unit renders at about
    // 0.73 px; the browser grows every label to 12 px or more on screen and cuts it to its plate (fitLabels).
    private const double BannerBig = 24, BannerSmall = 19;
    private const double Glyph = .6;

    private static readonly (int Q, int R)[] Dirs = [(1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1)];

    // The neighbour across edge i, where edge i runs from corner i to corner i + 1 (corner 0 at -30 degrees).
    private static readonly (int Q, int R)[] EdgeDirs = [(1, 0), (0, 1), (-1, 1), (-1, 0), (0, -1), (1, -1)];

    /// <summary>Banner colours, one per city: strong enough for a border on the dark land, never a heat colour.</summary>
    private static readonly string[] Colours =
    [
        "#e9b949", "#4fb3d9", "#c86fd6", "#6fcf8a", "#e8735a", "#8f9cf2", "#d9d26a", "#5ad1c1",
        "#f08fb4", "#a7c96b", "#f2a65a", "#7fb0ff", "#d78b6a", "#b59cf0", "#9fd7e8",
    ];

    protected override double ViewWidth => WorldW;
    protected override double ViewHeight => WorldH;

    /// <summary>The journey is the transition; the base class's own zoom snaps.</summary>
    protected override int ZoomMs => 0;

    protected override string RootLabel => "World map";

    private static (double X, double Y) Centre(int q, int r) => (Hw * (q + r / 2.0), 1.5 * Hr * r + Hr);

    private static int Distance((int Q, int R) a, (int Q, int R) b)
    {
        var dq = a.Q - b.Q;
        var dr = a.R - b.R;
        return (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(dq + dr)) / 2;
    }

    private static (double X, double Y) Corner(double cx, double cy, int i, double rad)
    {
        var a = Math.PI / 180 * (60 * i - 30);
        return (cx + rad * Math.Cos(a), cy + rad * Math.Sin(a));
    }

    private static void HexPath(StringBuilder sb, double cx, double cy, double rad, string fmt = "0.#")
    {
        for (var i = 0; i < 6; i++)
        {
            var (x, y) = Corner(cx, cy, i, rad);
            sb.Append(i == 0 ? 'M' : 'L').Append(x.ToString(fmt, CultureInfo.InvariantCulture)).Append(',')
                .Append(y.ToString(fmt, CultureInfo.InvariantCulture));
        }
        sb.Append('Z');
    }

    private static string HexPoints(double cx, double cy, double rad)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 6; i++)
        {
            var (x, y) = Corner(cx, cy, i, rad);
            if (i > 0) sb.Append(' ');
            sb.Append(F(x)).Append(',').Append(F(y));
        }
        return sb.ToString();
    }

    /// <summary>Tiles in rings 1..n around a centre, nearest first, walking each ring from its north-east corner.</summary>
    private static List<(int Q, int R)> Spiral((int Q, int R) c, int rings)
    {
        var list = new List<(int Q, int R)>();
        for (var k = 1; k <= rings; k++)
        {
            var h = (Q: c.Q + Dirs[4].Q * k, R: c.R + Dirs[4].R * k);
            for (var side = 0; side < 6; side++)
            {
                for (var j = 0; j < k; j++)
                {
                    list.Add(h);
                    h = (h.Q + Dirs[side].Q, h.R + Dirs[side].R);
                }
            }
        }
        return list;
    }

    private static int RingsFor(int districts) => districts <= 6 ? 1 : districts <= 18 ? 2 : 3;

    // ---- Deterministic noise for the terrain ----------------------------------------------------------------------

    private static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0xffffff) / (double)0xffffff;
        }
    }

    private static double Noise(double x, double y, int seed)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        double Fade(double t) => t * t * (3 - 2 * t);
        var fx = Fade(x - x0);
        var fy = Fade(y - y0);
        var a = Hash(x0, y0, seed) + (Hash(x0 + 1, y0, seed) - Hash(x0, y0, seed)) * fx;
        var b = Hash(x0, y0 + 1, seed) + (Hash(x0 + 1, y0 + 1, seed) - Hash(x0, y0 + 1, seed)) * fx;
        return a + (b - a) * fy;
    }

    // ---- Model -------------------------------------------------------------------------------------------------

    private enum Ground { Ocean, Coast, Grass, Plains, Forest, Hills }

    private sealed record Tile(int Q, int R, double X, double Y, Ground Ground, double Elevation);

    private sealed record Face(string Front, string Side, string Top);
    private sealed record Window(string Points, string Fill, bool Lit);

    /// <summary>A small building on a tile: three faces, its roof in the heat colour, lit windows.</summary>
    private sealed record House(Face Face, string Roof, List<Window> Windows, double Y);

    /// <summary>A district tile in a city: one of the repo's areas.</summary>
    private sealed record District(Hood Hood, double X, double Y, string Pad, List<House> Houses, int Heat)
    {
        public string Label => Hood.Label;
    }

    /// <summary>A tile the city works but no area sits on: a farm, or a mine on the hills.</summary>
    private sealed record Field(double X, double Y, bool Mine, string Pad);

    private sealed record City(
        string Repo, int Index, (int Q, int R) Hex, double X, double Y, int Rings, string Colour, int Prs, int Heat, string PrText,
        string Territory, string Border, string Walls, List<House> Houses, List<District> Districts, List<Field> Fields,
        double BannerY, double BannerFont, double BannerMax, (double X, double Y, double W, double H) View, (double L, double T, double R, double B) Box);

    private sealed record RoadLink(string A, string B, int W, string Path);

    private readonly List<Tile> _tiles = [];
    private readonly Dictionary<(int Q, int R), Tile> _tileAt = [];
    private readonly List<City> _cities = [];
    private readonly Dictionary<string, City> _cityByRepo = [];
    private readonly Dictionary<string, District> _districtByKey = [];
    private readonly List<RoadLink> _roads = [];
    private readonly List<string> _rivers = [];
    private readonly Dictionary<Ground, string> _groundPath = [];
    private string _trees = "", _hillMarks = "", _waves = "";

    /// <summary>The city the view travelled into (a repo click); null on the world map. Inside a district it is that district's repo.</summary>
    private string? _city;

    /// <summary>The city in view, in its centre or inside one of its districts.</summary>
    private string? Inside => CurrentHood?.Repo ?? _city;

    /// <summary>0 = world map (cities), 1 = inside a city (its districts), 2 = inside a district (its rooms).</summary>
    private int Level => CurrentHood is not null ? 2 : _city is not null ? 1 : 0;

    // ---- Build -------------------------------------------------------------------------------------------------

    protected override void Build(MapData data)
    {
        _tiles.Clear();
        _tileAt.Clear();
        _cities.Clear();
        _cityByRepo.Clear();
        _districtByKey.Clear();
        _roads.Clear();
        _rivers.Clear();
        _groundPath.Clear();

        // Places come from the full map (tests included), so hiding tests never moves a city or its districts.
        var layout = FullData ?? data;
        var places = PlaceCities(layout);
        MakeTerrain(places);
        var maxNodes = (layout.Repos ?? []).Select(r => r.Nodes).DefaultIfEmpty(1).Max();

        var index = 0;
        foreach (var repo in (layout.Repos ?? []).OrderByDescending(r => r.Nodes))
        {
            if (!places.TryGetValue(repo.Name, out var place)) continue;
            var shown = data.Repos?.FirstOrDefault(r => r.Name == repo.Name);
            var hoods = data.HoodsOf(repo.Name);
            if (shown is null || hoods.Count == 0) continue;
            var city = MakeCity(shown, index++, place.Hex, place.Rings, layout.HoodsOf(repo.Name), hoods, maxNodes);
            _cities.Add(city);
            _cityByRepo[city.Repo] = city;
            foreach (var d in city.Districts) _districtByKey[d.Hood.Key] = d;
        }
        // Back to front, so a southern city's banner sits over a northern city's walls.
        _cities.Sort((a, b) => a.Y.CompareTo(b.Y));
        BuildRoads(data);
    }

    /// <summary>
    /// Spreads the cities over the world, biggest first: each takes the open tile farthest from the cities already
    /// founded (capped, so they don't all flee to the coast), with a tile of open land between borders and no banner
    /// over another city.
    /// </summary>
    private Dictionary<string, ((int Q, int R) Hex, int Rings)> PlaceCities(MapData layout)
    {
        var placed = new Dictionary<string, ((int Q, int R) Hex, int Rings)>();
        var boxes = new List<(double L, double T, double R, double B)>();
        var candidates = new List<(int Q, int R)>();
        for (var r = 0; r <= (int)(WorldH / (1.5 * Hr)); r++)
        {
            for (var q = -r / 2 - 1; q <= (int)(WorldW / Hw) - r / 2 + 1; q++)
            {
                var (x, y) = Centre(q, r);
                if (x > 30 && x < WorldW - 30 && y > 30 && y < WorldH - 30) candidates.Add((q, r));
            }
        }
        var mid = (X: WorldW / 2, Y: WorldH / 2);
        foreach (var repo in (layout.Repos ?? []).OrderByDescending(r => r.Nodes))
        {
            var count = layout.HoodsOf(repo.Name).Count;
            if (count == 0) continue;
            var rings = RingsFor(count);
            var best = (Score: double.MinValue, Hex: (Q: 0, R: 0), Box: (L: 0.0, T: 0.0, R: 0.0, B: 0.0));
            foreach (var c in candidates)
            {
                var box = FootprintOf(c, rings, repo);
                if (box.L < 14 || box.R > WorldW - 14 || box.T < 10 || box.B > WorldH - 14) continue;
                var gap = 8.0;
                var ok = true;
                foreach (var (other, (hex, otherRings)) in placed)
                {
                    var d = Distance(c, hex) - rings - otherRings - 1;
                    if (d < 1) { ok = false; break; }
                    gap = Math.Min(gap, d);
                }
                if (!ok || boxes.Any(b => box.L < b.R + 8 && b.L < box.R + 8 && box.T < b.B + 4 && b.T < box.B + 4)) continue;
                var (x, y) = Centre(c.Q, c.R);
                var score = gap - double.Hypot((x - mid.X) / WorldW, (y - mid.Y) / WorldH) * 3;
                if (score > best.Score) best = (score, c, box);
            }
            if (best.Score == double.MinValue) continue;
            placed[repo.Name] = (best.Hex, rings);
            boxes.Add(best.Box);
        }
        return placed;
    }

    private static double BannerFontOf(RepoInfo repo) => repo.Nodes > 10000 ? BannerBig : BannerSmall;

    private static string PrTextOf(RepoInfo repo) => repo.Prs > 0 ? $"{repo.Prs} pull requests" : "quiet this month";

    /// <summary>Widest a city's banner may grow: room for about 22 characters plus the pull request badge.</summary>
    private static double BannerWidth(RepoInfo repo) => (Math.Min(repo.Name.Length, 22) * Glyph + 2.4) * BannerFontOf(repo) + 16;

    /// <summary>Where the banner hangs: over the centre tile, a little north.</summary>
    private static double BannerYOf(double cy) => cy - Hr * 1.55;

    /// <summary>The ground a city and its banner cover.</summary>
    private static (double L, double T, double R, double B) FootprintOf((int Q, int R) c, int rings, RepoInfo repo)
    {
        var (x, y) = Centre(c.Q, c.R);
        var half = Math.Max((rings + .5) * Hw, BannerWidth(repo) / 2);
        var top = Math.Min(y - (rings * 1.5 + 1) * Hr, BannerYOf(y) - BannerFontOf(repo));
        return (x - half, top, x + half, y + (rings * 1.5 + 1) * Hr);
    }

    /// <summary>Land rises around every city and falls away to sea at the world's edge; noise does the rest.</summary>
    private void MakeTerrain(Dictionary<string, ((int Q, int R) Hex, int Rings)> places)
    {
        var owned = new Dictionary<(int Q, int R), int>();
        foreach (var (_, (hex, rings)) in places)
        {
            owned[hex] = rings;
            foreach (var t in Spiral(hex, rings + 1)) owned.TryAdd(t, rings);
        }
        var rows = (int)(WorldH / (1.5 * Hr)) + Bleed;
        for (var r = -Bleed; r <= rows; r++)
        {
            for (var q = -r / 2 - Bleed - 1; q <= (int)(WorldW / Hw) - r / 2 + Bleed; q++)
            {
                var (x, y) = Centre(q, r);
                if (x < -Hw * Bleed || x > WorldW + Hw * Bleed) continue;
                var e = Noise(x / 230, y / 230, 7) * .62 + Noise(x / 95, y / 95, 11) * .28 + Noise(x / 40, y / 40, 13) * .1;
                foreach (var (_, (hex, rings)) in places)
                {
                    var d = Distance((q, r), hex);
                    e += .42 * Math.Max(0, 1 - d / (rings + 3.5));
                }
                var edge = Math.Min(Math.Min(x, WorldW - x), Math.Min(y, WorldH - y));
                e -= .55 * Math.Clamp(1 - edge / 120, 0, 1.4);
                if (owned.ContainsKey((q, r))) e = Math.Max(e, .5);
                var m = Noise(x / 150 + 40, y / 150 + 40, 29);
                var ground = e < .4 ? Ground.Ocean
                    : e > .78 && !owned.ContainsKey((q, r)) ? Ground.Hills
                    : m > .6 && !owned.ContainsKey((q, r)) ? Ground.Forest
                    : m > .42 ? Ground.Grass : Ground.Plains;
                var tile = new Tile(q, r, x, y, ground, e);
                _tiles.Add(tile);
                _tileAt[(q, r)] = tile;
            }
        }
        // Sea next to land is the shallow coast.
        foreach (var t in _tiles.Where(t => t.Ground == Ground.Ocean).ToList())
        {
            if (Dirs.Any(d => _tileAt.TryGetValue((t.Q + d.Q, t.R + d.R), out var n) && n.Ground is not (Ground.Ocean or Ground.Coast)))
            {
                var coast = t with { Ground = Ground.Coast };
                _tileAt[(t.Q, t.R)] = coast;
            }
        }
        _tiles.Clear();
        _tiles.AddRange(_tileAt.Values.OrderBy(t => t.R).ThenBy(t => t.Q));

        var paths = new Dictionary<Ground, StringBuilder>();
        var trees = new StringBuilder();
        var hills = new StringBuilder();
        var waves = new StringBuilder();
        foreach (var t in _tiles)
        {
            if (!paths.TryGetValue(t.Ground, out var sb)) paths[t.Ground] = sb = new StringBuilder();
            HexPath(sb, t.X, t.Y, Hr + .4);
            var h = Hash(t.Q, t.R, 3);
            switch (t.Ground)
            {
                case Ground.Forest:
                    for (var k = 0; k < 3; k++)
                    {
                        var tx = t.X + (k - 1) * Hr * .5 + (Hash(t.Q, t.R, k) - .5) * 4;
                        var ty = t.Y + (k == 1 ? -Hr * .25 : Hr * .15) + (Hash(t.R, t.Q, k) - .5) * 4;
                        trees.Append(FormattableString.Invariant($"M{tx - 5:0.#},{ty + 6:0.#}L{tx:0.#},{ty - 8:0.#}L{tx + 5:0.#},{ty + 6:0.#}Z"));
                    }
                    break;
                case Ground.Hills:
                    hills.Append(FormattableString.Invariant($"M{t.X - 12:0.#},{t.Y + 5:0.#}Q{t.X - 5:0.#},{t.Y - 8:0.#} {t.X + 2:0.#},{t.Y + 5:0.#}"));
                    hills.Append(FormattableString.Invariant($"M{t.X - 1:0.#},{t.Y + 7:0.#}Q{t.X + 6:0.#},{t.Y - 4:0.#} {t.X + 13:0.#},{t.Y + 7:0.#}"));
                    break;
                case Ground.Ocean when h > .82:
                    waves.Append(FormattableString.Invariant($"M{t.X - 8:0.#},{t.Y:0.#}q4,-4 8,0t8,0"));
                    break;
            }
        }
        foreach (var (g, sb) in paths) _groundPath[g] = sb.ToString();
        _trees = trees.ToString();
        _hillMarks = hills.ToString();
        _waves = waves.ToString();
        MakeRivers(owned);
    }

    /// <summary>Two rivers, each running downhill from high open ground to the sea.</summary>
    private void MakeRivers(Dictionary<(int Q, int R), int> owned)
    {
        var used = new HashSet<(int Q, int R)>();
        var springs = _tiles.Where(t => t.Ground is Ground.Hills or Ground.Forest && t.X > 60 && t.X < WorldW - 60 && t.Y > 60 && t.Y < WorldH - 60)
            .OrderByDescending(t => t.Elevation).ToList();
        foreach (var spring in springs)
        {
            if (_rivers.Count == 2) break;
            if (used.Any(u => Distance(u, (spring.Q, spring.R)) < 8)) continue;
            var course = new List<Tile> { spring };
            var here = spring;
            for (var i = 0; i < 40 && here.Ground is not (Ground.Ocean or Ground.Coast); i++)
            {
                var next = Dirs.Select(d => _tileAt.GetValueOrDefault((here.Q + d.Q, here.R + d.R)))
                    .Where(n => n is not null && !course.Contains(n) && !owned.ContainsKey((n.Q, n.R)) || n is { Ground: Ground.Coast or Ground.Ocean })
                    .OrderBy(n => n!.Elevation).FirstOrDefault();
                if (next is null || next.Elevation > here.Elevation + .02) break;
                course.Add(next);
                here = next;
            }
            if (course.Count < 5 || here.Ground is not (Ground.Ocean or Ground.Coast)) continue;
            foreach (var t in course) used.Add((t.Q, t.R));
            var sb = new StringBuilder();
            sb.Append(FormattableString.Invariant($"M{course[0].X:0.#},{course[0].Y:0.#}"));
            for (var i = 1; i < course.Count; i++)
            {
                var a = course[i - 1];
                var b = course[i];
                var bend = (Hash(a.Q, b.R, 5) - .5) * Hr * .8;
                sb.Append(FormattableString.Invariant($"Q{(a.X + b.X) / 2 + bend:0.#},{(a.Y + b.Y) / 2 - bend * .5:0.#} {b.X:0.#},{b.Y:0.#}"));
            }
            _rivers.Add(sb.ToString());
        }
    }

    private static string HeatVar(int level) => $"var(--heat-{level})";

    private static (double X, double Y) Iso(double ox, double oy, double x, double y, double z) =>
        (ox + (x - y) * .866, oy + (x + y) * .5 - z);

    private static string Poly(double ox, double oy, params (double X, double Y, double Z)[] pts)
    {
        var sb = new StringBuilder();
        foreach (var (x, y, z) in pts)
        {
            var p = Iso(ox, oy, x, y, z);
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(F(p.X)).Append(',').Append(F(p.Y));
        }
        return sb.ToString();
    }

    /// <summary>A little isometric house standing at (ox, oy): footprint w by w, height ht, windows lit by the heat.</summary>
    private static House MakeHouse(double ox, double oy, double w, double ht, int heat, bool cool, int seed)
    {
        var x = -w / 2;
        var y = -w / 2;
        var face = new Face(
            Poly(ox, oy, (x, y + w, 0), (x + w, y + w, 0), (x + w, y + w, ht), (x, y + w, ht)),
            Poly(ox, oy, (x + w, y, 0), (x + w, y + w, 0), (x + w, y + w, ht), (x + w, y, ht)),
            Poly(ox, oy, (x, y, ht), (x + w, y, ht), (x + w, y + w, ht), (x, y + w, ht)));
        var roof = heat > 0 ? HeatVar(heat) : cool ? "var(--stone-x3)" : "var(--stone-3)";
        var windows = new List<Window>();
        var share = heat == 0 ? 0 : Math.Min(1, .25 + heat * .13);
        var rows = Math.Max(1, (int)((ht - 3) / 5));
        for (var r = 0; r < rows; r++)
        {
            var lit = Hash(seed, r, 9) < share;
            var z = 2 + r * 5;
            var fill = lit ? HeatVar(Math.Max(2, heat)) : "var(--heat-0)";
            windows.Add(new Window(Poly(ox, oy, (x + w * .25, y + w, z), (x + w * .55, y + w, z), (x + w * .55, y + w, z + 2.4), (x + w * .25, y + w, z + 2.4)), fill, lit));
            windows.Add(new Window(Poly(ox, oy, (x + w, y + w * .3, z), (x + w, y + w * .6, z), (x + w, y + w * .6, z + 2.4), (x + w, y + w * .3, z + 2.4)), fill, lit));
        }
        return new House(face, roof, windows, oy);
    }

    private City MakeCity(RepoInfo repo, int index, (int Q, int R) hex, int rings, IReadOnlyList<Hood> layoutHoods, IReadOnlyList<Hood> hoods, int maxNodes)
    {
        var (cx, cy) = Centre(hex.Q, hex.R);
        var heat = MapData.Heat(repo.Prs);
        var cool = repo.Name != MapData.MudBlazorRepo;
        var colour = Colours[index % Colours.Length];
        var ring = Spiral(hex, rings);
        var owned = new HashSet<(int Q, int R)>(ring) { hex };

        // Territory tint and its outer border (the edges with no owned tile beyond them).
        var territory = new StringBuilder();
        var border = new StringBuilder();
        foreach (var t in owned)
        {
            var (x, y) = Centre(t.Q, t.R);
            HexPath(territory, x, y, Hr + .4);
            for (var i = 0; i < 6; i++)
            {
                if (owned.Contains((t.Q + EdgeDirs[i].Q, t.R + EdgeDirs[i].R))) continue;
                var a = Corner(x, y, i, Hr - 1.6);
                var b = Corner(x, y, (i + 1) % 6, Hr - 1.6);
                border.Append(FormattableString.Invariant($"M{a.X:0.##},{a.Y:0.##}L{b.X:0.##},{b.Y:0.##}"));
            }
        }

        // Districts keep their tile from the full map: the biggest areas nearest the centre.
        var slot = layoutHoods.Select((h, i) => (h.Key, i)).ToDictionary(t => t.Key, t => t.i);
        var districts = new List<District>();
        var taken = new HashSet<int>();
        foreach (var h in hoods)
        {
            var i = slot.GetValueOrDefault(h.Key, -1);
            if (i < 0 || i >= ring.Count) continue;
            taken.Add(i);
            var (x, y) = Centre(ring[i].Q, ring[i].R);
            var level = MapData.Heat(h.Prs);
            var size = Math.Min(1.2, Math.Sqrt(h.Size / 3000.0));
            var houses = new List<House>();
            var n = 1 + (int)Math.Round(size * 2);
            for (var k = 0; k < n; k++)
            {
                var a = Math.PI * 2 * k / n + Hash(ring[i].Q, ring[i].R, 1);
                var off = n == 1 ? 0 : Hr * .36;
                houses.Add(MakeHouse(x + Math.Cos(a) * off, y + Math.Sin(a) * off * .7 + 3, 5 + size * 3, 6 + size * 10 + Hash(k, i, 2) * 4, level, cool, i * 7 + k));
            }
            houses.Sort((p, q2) => p.Y.CompareTo(q2.Y));
            districts.Add(new District(h, x, y, HexPoints(x, y, Hr * .82), houses, level));
        }
        var fields = new List<Field>();
        for (var i = 0; i < ring.Count; i++)
        {
            if (taken.Contains(i) || slot.ContainsValue(i)) continue;
            var (x, y) = Centre(ring[i].Q, ring[i].R);
            fields.Add(new Field(x, y, Hash(ring[i].Q, ring[i].R, 4) > .7, HexPoints(x, y, Hr * .8)));
        }

        // The centre: walls round a cluster of houses, taller and more of them for a bigger code base.
        var bulk = Math.Sqrt(Math.Max(1, repo.Nodes) / (double)Math.Max(1, maxNodes));
        var centreHouses = new List<House>();
        var count = 3 + (int)Math.Round(bulk * 5);
        for (var k = 0; k < count; k++)
        {
            var a = Math.PI * 2 * k / count + .4;
            var off = k == 0 ? 0 : Hr * (.32 + .12 * (k % 2));
            var ht = k == 0 ? 14 + bulk * 26 : 7 + bulk * 10 + Hash(k, index, 6) * 5;
            centreHouses.Add(MakeHouse(cx + Math.Cos(a) * off, cy + Math.Sin(a) * off * .65 + 4, k == 0 ? 8 : 6, ht, heat, cool, index * 31 + k));
        }
        centreHouses.Sort((p, q2) => p.Y.CompareTo(q2.Y));

        var font = BannerFontOf(repo);
        var tiles = owned.Select(t => Centre(t.Q, t.R)).ToList();
        var bannerY = BannerYOf(cy);
        var box = (L: tiles.Min(t => t.X) - Hw / 2, T: Math.Min(tiles.Min(t => t.Y) - Hr, bannerY - font), R: tiles.Max(t => t.X) + Hw / 2, B: tiles.Max(t => t.Y) + Hr);

        // The city view: the whole territory with a little ground round it, at the stage's shape, kept on the world.
        // The banner is left out (it gives way to the district names inside the city).
        var land = (L: tiles.Min(t => t.X) - Hw / 2, T: tiles.Min(t => t.Y) - Hr, R: tiles.Max(t => t.X) + Hw / 2, B: tiles.Max(t => t.Y) + Hr);
        var vw = Math.Max((land.R - land.L) * 1.16, (land.B - land.T) * 1.1 * WorldW / WorldH);
        vw = Math.Max(vw, 260);
        var vh = vw * WorldH / WorldW;
        var vx = Math.Clamp(cx - vw / 2, -Hw * 2, WorldW + Hw * 2 - vw);
        var vy = Math.Clamp((land.T + land.B) / 2 - vh / 2, -Hr * 2, WorldH + Hr * 2 - vh);

        return new City(repo.Name, index, hex, cx, cy, rings, colour, repo.Prs, heat, PrTextOf(repo), territory.ToString(), border.ToString(),
            HexPoints(cx, cy, Hr * .86), centreHouses, districts, fields, bannerY, font, BannerWidth(repo), (vx, vy, vw, vh), box);
    }

    /// <summary>Trade routes between cities whose areas call each other: a road from centre to centre along the tiles.</summary>
    private void BuildRoads(MapData data)
    {
        var weights = new Dictionary<(string, string), int>();
        var repoOf = data.Hoods.ToDictionary(h => h.Key, h => h.Repo);
        foreach (var r in data.Roads ?? [])
        {
            if (!repoOf.TryGetValue(r.A, out var a) || !repoOf.TryGetValue(r.B, out var b) || a == b) continue;
            if (!_cityByRepo.ContainsKey(a) || !_cityByRepo.ContainsKey(b)) continue;
            var key = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
            weights[key] = weights.GetValueOrDefault(key) + r.W;
        }
        foreach (var ((a, b), w) in weights.OrderByDescending(kv => kv.Value))
        {
            var ca = _cityByRepo[a];
            var cb = _cityByRepo[b];
            // Walk the straight line in hex steps, then round the corners.
            var n = Distance(ca.Hex, cb.Hex);
            var pts = new List<(double X, double Y)>();
            for (var i = 0; i <= n; i++)
            {
                var t = n == 0 ? 0 : i / (double)n;
                var q = ca.Hex.Q + (cb.Hex.Q - ca.Hex.Q) * t + 1e-6;
                var r = ca.Hex.R + (cb.Hex.R - ca.Hex.R) * t + 1e-6;
                var h = RoundHex(q, r);
                var c = Centre(h.Q, h.R);
                if (pts.Count == 0 || pts[^1] != c) pts.Add(c);
            }
            var sb = new StringBuilder();
            sb.Append(FormattableString.Invariant($"M{pts[0].X:0.#},{pts[0].Y:0.#}"));
            for (var i = 1; i < pts.Count - 1; i++)
            {
                var m = ((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
                sb.Append(FormattableString.Invariant($"Q{pts[i].X:0.#},{pts[i].Y:0.#} {m.Item1:0.#},{m.Item2:0.#}"));
            }
            sb.Append(FormattableString.Invariant($"L{pts[^1].X:0.#},{pts[^1].Y:0.#}"));
            _roads.Add(new RoadLink(a, b, w, sb.ToString()));
        }
    }

    private static (int Q, int R) RoundHex(double q, double r)
    {
        var s = -q - r;
        var rq = Math.Round(q);
        var rr = Math.Round(r);
        var rs = Math.Round(s);
        var dq = Math.Abs(rq - q);
        var dr = Math.Abs(rr - r);
        var ds = Math.Abs(rs - s);
        if (dq > dr && dq > ds) rq = -rr - rs;
        else if (dr > ds) rr = -rq - rs;
        return ((int)rq, (int)rr);
    }

    // ---- Districts and rooms --------------------------------------------------------------------------------------

    /// <summary>
    /// Inside a district its rooms open as a honeycomb around the district's tile, rows of up to six. Focus returns the
    /// centre and a radius that makes the shared view exactly frame that honeycomb and its heading.
    /// </summary>
    protected override (double X, double Y, double Rx, double Ry) Focus(Hood hood)
    {
        var p = RoomPanel(hood);
        var vw = Math.Max(p.W * 1.12, p.H * 1.12 * WorldW / WorldH);
        return (p.X + p.W / 2, p.Y + p.H / 2, vw / 3.4, 1e-3);
    }

    /// <summary>Room hex radius in map units: about a sixth of a district tile, which the zoom makes about 90 px.</summary>
    private const double RoomR = Hr * .42;

    private static List<int> RoomRows(int n)
    {
        var cols = Math.Clamp((int)Math.Ceiling(Math.Sqrt(n * 1.6)), 1, 6);
        var rows = new List<int>();
        var left = n;
        for (var i = 0; left > 0; i++)
        {
            var c = Math.Min(left, cols > 1 && i % 2 == 1 ? cols - 1 : cols);
            rows.Add(c);
            left -= c;
        }
        return rows;
    }

    /// <summary>The rooms' panel round a district: heading strip on top, honeycomb below. X/Y is the top-left corner.</summary>
    private (double X, double Y, double W, double H, double Head) RoomPanel(Hood hood)
    {
        var d = _districtByKey.GetValueOrDefault(hood.Key);
        var (x, y) = d is null ? (WorldW / 2, WorldH / 2) : (d.X, d.Y);
        var n = Math.Max(1, (Data ?? FullData)?.ClustersOf(hood.Key).Count ?? 1);
        var rows = RoomRows(n);
        var cols = rows.Max();
        var w = cols * Math.Sqrt(3) * RoomR * 1.06 + RoomR * 1.2;
        var head = RoomR * .95;
        var h = head + (rows.Count - 1) * 1.5 * RoomR * 1.06 + 2 * RoomR + RoomR * .8;
        return (x - w / 2, y - h / 2 - head * .3, w, h, head);
    }

    protected override List<MapRoom> LayoutRooms(Hood hood, (double X, double Y, double Rx, double Ry) f)
    {
        var clusters = Data!.ClustersOf(hood.Key);
        if (clusters.Count == 0) return [];
        var p = RoomPanel(hood);
        var rows = RoomRows(clusters.Count);
        var dx = Math.Sqrt(3) * RoomR * 1.06;
        var dy = 1.5 * RoomR * 1.06;
        var top = p.Y + p.Head + RoomR * 1.4;
        var result = new List<MapRoom>();
        var i = 0;
        var cols = rows.Max();
        var gridLeft = p.X + p.W / 2 - (cols - 1) * dx / 2;
        for (var row = 0; row < rows.Count; row++)
        {
            // Stay on the honeycomb: odd rows sit half a cell over, and a short last row is centred by whole cells.
            var room = row % 2 == 1 && cols > 1 ? cols - 1 : cols;
            var left = gridLeft + (row % 2 == 1 ? dx / 2 : 0) + (room - rows[row]) / 2 * dx;
            for (var c = 0; c < rows[row]; c++, i++)
            {
                var cl = clusters[i];
                var full = MapData.RoomName(cl);
                var shortName = full.Contains(" · ") ? full[(full.IndexOf(" · ", StringComparison.Ordinal) + 3)..] : full;
                shortName = System.Text.RegularExpressions.Regex.Replace(shortName, "^(MudEx|MudX|Mud)(?=[A-Z])", "");
                result.Add(new MapRoom(cl, shortName, full, left + c * dx, top + row * dy, RoomR, RoomR * .3));
            }
        }
        return result;
    }

    // ---- Lifecycle and travel ---------------------------------------------------------------------------------

    protected override async Task OnMapRenderedAsync()
    {
        _module = await JS.InvokeAsync<IJSObjectReference>("import", "./city.js");
        await _module.InvokeVoidAsync("init", SharedAssets.ZoomModule, typeof(Home).Assembly.ManifestModule.ModuleVersionId.ToString("N"));
        await _module.InvokeVoidAsync("start", Svg);
    }

    protected override async Task OnMapUpdatedAsync()
    {
        if (_module is not null) await _module.InvokeVoidAsync("start", Svg);
    }

    private bool _demoHandled;
    private bool _deepLinkHandled;
    private bool _riding;
    private string? _lastCity;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (Data is null || _module is null) return;
        if (!_deepLinkHandled)
        {
            _deepLinkHandled = true;
            var q = HttpUtility.ParseQueryString(new Uri(Nav.Uri).Query);
            // ?repo=<name> lands inside that city (the base page has already opened its sheet).
            // (The repo's display name, or its owner/name as research links may use.)
            var linked = q["repo"] is { } asked ? Data.Repos?.FirstOrDefault(r => r.FullName == asked)?.Name ?? asked : null;
            if (CurrentHood is null && linked is { } repo && _cityByRepo.TryGetValue(repo, out var city))
            {
                _city = repo;
                _lastCity = repo;
                await _module.InvokeVoidAsync("jump", Svg, ViewOf(city.View));
                StateHasChanged();
            }
            if (CurrentHood is not null) _city = CurrentHood.Repo;
            // On a phone the world pans sideways: start with its middle in the window.
            await _module.InvokeVoidAsync("centre", Svg);
        }
        await _module.InvokeVoidAsync("fit", Svg);
        if (_demoHandled) return;
        var query = HttpUtility.ParseQueryString(new Uri(Nav.Uri).Query);
        if (query["ridedemo"] is not { Length: > 0 } demo) return;
        _demoHandled = true;
        // Screenshot hook: start the journey into a city (?ridecity=<repo>, MudBlazor by default) and freeze it at
        // ?ridedemo=<milliseconds>.
        if (!int.TryParse(demo, out var freeze)) freeze = 1800;
        var target = _cityByRepo.GetValueOrDefault(query["ridecity"] ?? MapData.MudBlazorRepo) ?? _cities.FirstOrDefault();
        if (target is not null) await _module.InvokeAsync<double>("travel", Svg, IntoCity(target), freeze);
    }

    private static double[] ViewOf((double X, double Y, double W, double H) v) => [v.X, v.Y, v.W, v.H];

    private (double X, double Y, double W, double H) DistrictView(Hood hood) => HoodView(Focus(hood));

    /// <summary>Where a caravan heading for <paramref name="city"/> sets out: the last city it visited, or a trade partner.</summary>
    private (double X, double Y) SetOutFor(City city)
    {
        if (_lastCity is { } last && last != city.Repo && _cityByRepo.TryGetValue(last, out var from)) return (from.X, from.Y);
        var partner = _roads.Where(r => r.A == city.Repo || r.B == city.Repo).Select(r => r.A == city.Repo ? r.B : r.A)
            .Select(n => _cityByRepo.GetValueOrDefault(n)).FirstOrDefault(c => c is not null);
        partner ??= _cities.Where(c => c != city).OrderBy(c => double.Hypot(c.X - city.X, c.Y - city.Y)).FirstOrDefault();
        return partner is null ? (WorldW / 2, WorldH / 2) : (partner.X, partner.Y);
    }

    private object IntoCity(City city)
    {
        var from = SetOutFor(city);
        return new
        {
            dir = 1, from = new[] { from.X, from.Y }, to = new[] { city.X, city.Y }, view = ViewOf(city.View),
            kicker = "Now entering", label = city.Repo, sub = city.PrText, colour = city.Colour, target = city.Repo,
        };
    }

    /// <summary>
    /// Plays a journey and swaps the level at the right moment: going in, the camera arrives first and the new level
    /// appears under the unfurling banner; going out, the new level is drawn first and the camera pulls back to it.
    /// Skip animations (or no script yet) jumps straight there with a short fade.
    /// </summary>
    private async Task TravelAsync(object opts, double[] view, bool inward, Func<Task> swap)
    {
        if (_module is null || _riding || SkipAnimations)
        {
            await swap();
            StateHasChanged();
            await Task.Yield();
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("jump", Svg, view);
                await _module.InvokeVoidAsync("centre", Svg);
                await _module.InvokeVoidAsync("fit", Svg);
            }
            if (SkipAnimations) await Zoom.FadeAsync(Svg);
            return;
        }
        _riding = true;
        try
        {
            if (!inward)
            {
                await swap();
                StateHasChanged();
                await Task.Yield();
                await _module.InvokeVoidAsync("fit", Svg);
            }
            await _module.InvokeAsync<double>("travel", Svg, opts, -1);
            if (inward)
            {
                await swap();
                StateHasChanged();
                await Task.Yield();
            }
            await _module.InvokeVoidAsync("centre", Svg);
            await _module.InvokeVoidAsync("fit", Svg);
            await _module.InvokeVoidAsync("arrive", Svg);
        }
        finally { _riding = false; }
    }

    /// <summary>
    /// A city click: the caravan travels the road there, the camera follows, and the city opens with its districts named.
    /// The repo sheet stays closed so the city has the whole stage; its centre tile (or a second click) opens it.
    /// </summary>
    private Task TravelToCityAsync(string repo)
    {
        if (!_cityByRepo.TryGetValue(repo, out var city)) return Task.CompletedTask;
        if (Level == 1 && _city == repo)
        {
            OpenRepo(repo);
            return Task.CompletedTask;
        }
        if (Level == 2 && CurrentHood is { } hood && _districtByKey.TryGetValue(hood.Key, out var d))
        {
            // Out of a district, back to its city centre.
            var back = new
            {
                dir = -1, from = new[] { d.X, d.Y }, to = new[] { city.X, city.Y }, view = ViewOf(city.View),
                kicker = "Back to the city centre", label = repo, sub = city.PrText, colour = city.Colour, target = (string?)null,
            };
            return TravelAsync(back, ViewOf(city.View), false, () =>
            {
                CurrentHood = null;
                CurrentRoom = null;
                CurrentRepo = null;
                Rooms = [];
                _city = repo;
                SetTrail();
                return Task.CompletedTask;
            });
        }
        return TravelAsync(IntoCity(city), ViewOf(city.View), true, () =>
        {
            CurrentHood = null;
            CurrentRoom = null;
            CurrentRepo = null;
            Rooms = [];
            _city = repo;
            _lastCity = repo;
            SetTrail();
            return Task.CompletedTask;
        });
    }

    /// <summary>A district click inside a city: from the city centre to the district's tile, which opens into its rooms.</summary>
    private Task TravelToDistrictAsync(Hood hood)
    {
        if (CurrentHood?.Key == hood.Key || !_cityByRepo.TryGetValue(hood.Repo, out var city) || !_districtByKey.TryGetValue(hood.Key, out var d))
        {
            return Task.CompletedTask;
        }
        var view = DistrictView(hood);
        var opts = new
        {
            dir = 1, from = new[] { city.X, city.Y }, to = new[] { d.X, d.Y }, view = ViewOf(view),
            kicker = $"{hood.Repo} district", label = hood.Label, sub = $"{Data!.ClustersOf(hood.Key).Count} rooms · {hood.Prs} pull requests",
            colour = city.Colour, target = (string?)null,
        };
        return TravelAsync(opts, ViewOf(view), true, async () =>
        {
            _city = hood.Repo;
            await OpenHoodAsync(hood);
        });
    }

    // ---- Research: where "Show on map" goes --------------------------------------------------------------------

    protected override bool ResearchEnabled => true;

    protected override Task GoToRepoAsync(string repo) =>
        Level == 1 && _city == repo ? Task.CompletedTask : TravelToCityAsync(repo);

    protected override Task GoToHoodAsync(Hood hood) => TravelToDistrictAsync(hood);

    protected override Task GoToWorldAsync() => HomeAsync();

    /// <summary>Back to the world map: the caravan leaves the city on its road while the camera pulls back over the world.</summary>
    protected override Task HomeAsync()
    {
        if (Level == 0) return Task.CompletedTask;
        var city = Inside is { } repo ? _cityByRepo.GetValueOrDefault(repo) : null;
        var start = CurrentHood is { } hood && _districtByKey.TryGetValue(hood.Key, out var d) ? (d.X, d.Y) : city is null ? (WorldW / 2, WorldH / 2) : (city.X, city.Y);
        var away = city is null ? (WorldW / 2, WorldH / 2) : SetOutFor(city);
        var to = (start.Item1 + (away.Item1 - start.Item1) * .45, start.Item2 + (away.Item2 - start.Item2) * .45);
        var opts = new
        {
            dir = -1, from = new[] { start.Item1, start.Item2 }, to = new[] { to.Item1, to.Item2 }, view = new[] { 0, 0, WorldW, WorldH },
            kicker = "Back to", label = "The world map", sub = $"{_cities.Count} cities", colour = city?.Colour ?? "#fe6a00", target = (string?)null,
        };
        return TravelAsync(opts, [0, 0, WorldW, WorldH], false, () =>
        {
            CurrentHood = null;
            CurrentRoom = null;
            CurrentRepo = null;
            Rooms = [];
            _city = null;
            SetTrail();
            return Task.CompletedTask;
        });
    }

    /// <summary>After a redraw (the tests toggle or a live update), put the camera back on the level in view.</summary>
    protected override async Task RestoreViewAsync()
    {
        if (_module is null) return;
        if (Level == 1 && _city is { } c && _cityByRepo.TryGetValue(c, out var city)) await _module.InvokeVoidAsync("jump", Svg, ViewOf(city.View));
        else if (Level == 2 && CurrentHood is { } hood) await _module.InvokeVoidAsync("jump", Svg, ViewOf(DistrictView(hood)));
        else if (Level == 0) await _module.InvokeVoidAsync("jump", Svg, new[] { 0, 0, WorldW, WorldH });
    }

    /// <summary>Clicking open land goes up one level: from a district to its city, from a city to the world map.</summary>
    private Task BackgroundAsync() => Level switch
    {
        2 => TravelToCityAsync(CurrentHood!.Repo),
        1 => HomeAsync(),
        _ => Task.CompletedTask,
    };

    /// <summary>Trail: World map › city › district › room, each step travelling back to its level.</summary>
    private IReadOnlyList<ZoomTrail.Step> CityTrail
    {
        get
        {
            var steps = new List<ZoomTrail.Step> { new(RootLabel, EventCallback.Factory.Create(this, HomeAsync)) };
            if (Inside is { } city)
            {
                steps.Add(new(city, EventCallback.Factory.Create(this, () => TravelToCityAsync(city)), city));
            }
            if (CurrentHood is { } hood)
            {
                steps.Add(new(hood.Label, EventCallback.Factory.Create(this, CloseRoom), hood.Repo));
            }
            if (CurrentRoom is { } room)
            {
                steps.Add(new(MapData.RoomName(room), EventCallback.Factory.Create(this, () => { }), CurrentHood?.Repo));
            }
            return steps;
        }
    }

    public async ValueTask DisposeAsync()
    {
        DetachResearch();
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
