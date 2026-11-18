using System.Text;
using MudMaps.Shared;

namespace MudMaps.Islands.Pages;

public partial class Home : MapPageBase
{
    private const double Squash = 0.8;

    private static readonly Dictionary<string, (double X, double Y)> Waters = new()
    {
        ["MudBlazor"] = (400, 470),
        ["MudBlazor.Extensions"] = (1080, 420),
        ["CodeBeam Extensions"] = (1430, 700),
        ["MudX"] = (130, 860),
        ["TryMudBlazor"] = (390, 890),
        ["Heron Calendar"] = (640, 870),
        ["Templates"] = (880, 890),
        ["Static Inputs"] = (1110, 870),
        ["ThemeManager"] = (1290, 920),
    };

    private sealed record Flag(double X, double Y);
    private sealed record Island(Hood Hood, double X, double Y, double R, string Shore, string Coast, List<string> Rings, List<Flag> Flags);
    private sealed record Route(string Path, double Width, bool Cross);
    private sealed record GroupLabel(string Repo, double X, double Y, int Prs);

    private readonly List<Island> _islands = [];
    private readonly List<Route> _routes = [];
    private readonly List<GroupLabel> _groups = [];
    private readonly Dictionary<string, Island> _byKey = [];

    protected override string RootLabel => "Whole chart";
    protected override (string Prs, string Merged, string Repos) FigureLabels => ("flags raised", "made port", "islands surveyed");

    protected override (double X, double Y, double Rx, double Ry) Focus(Hood hood)
    {
        var i = _byKey[hood.Key];
        var rx = Math.Max(i.R * 1.4, 40);
        return (i.X, i.Y, rx, rx * .62);
    }

    protected override void Build(MapData data)
    {
        _islands.Clear();
        _routes.Clear();
        _groups.Clear();
        _byKey.Clear();
        var hoods = data.Hoods.Where(h => Waters.ContainsKey(h.Repo)).ToList();
        var nodes = hoods.Select(h => new LayoutNode(h.Key, h.Repo, 10 + Math.Sqrt(h.Size) * 0.55)).ToList();
        ForceLayout.Relax(nodes, Waters, data.Roads, (20, 170, 1580, 975), gap: 34, squash: Squash, iterations: 700, pull: 0.01);
        var byKey = hoods.ToDictionary(h => h.Key);

        var seed = 1;
        foreach (var n in nodes)
        {
            var h = byKey[n.Key];
            seed++;
            var rings = new List<string>();
            var count = Math.Min(5, 1 + h.Size / 500);
            for (var r = 1; r <= count; r++) rings.Add(Blob(n.X + 2 * r, n.Y - 2 * r, n.R, seed, 1 - r * .17));
            var flags = new List<Flag>();
            var nf = Math.Min(8, (int)Math.Ceiling(h.Prs / 4.0));
            for (var f = 0; f < nf; f++)
            {
                var a = f / (double)Math.Max(1, nf) * Math.PI * 2 + .4;
                flags.Add(new Flag(n.X + Math.Cos(a) * n.R * .55, n.Y + Math.Sin(a) * n.R * .42));
            }
            var island = new Island(h, n.X, n.Y, n.R, Blob(n.X, n.Y, n.R, seed, 1.25), Blob(n.X, n.Y, n.R, seed, 1), rings, flags);
            _islands.Add(island);
            _byKey[h.Key] = island;
        }

        var roads = data.Roads.Where(r => _byKey.ContainsKey(r.A) && _byKey.ContainsKey(r.B)).Take(100).ToList();
        var maxW = roads.Max(r => r.W);
        foreach (var r in roads)
        {
            var a = _byKey[r.A];
            var b = _byKey[r.B];
            var mx = (a.X + b.X) / 2;
            var my = (a.Y + b.Y) / 2 - 30;
            _routes.Add(new Route($"M{F(a.X)},{F(a.Y)} Q{F(mx)},{F(my)} {F(b.X)},{F(b.Y)}", 1 + 2.5 * Math.Sqrt(r.W / (double)maxW), a.Hood.Repo != b.Hood.Repo));
        }

        foreach (var repo in data.Repos ?? [])
        {
            var group = _islands.Where(i => i.Hood.Repo == repo.Name).ToList();
            if (group.Count == 0) continue;
            var cx = group.Average(i => i.X);
            var top = group.Min(i => i.Y - i.R * Squash * 1.3);
            _groups.Add(new GroupLabel(repo.Name, cx, Math.Max(160, top - 12), repo.Prs));
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
