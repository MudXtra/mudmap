using System.Text;
using MudMaps.Shared;

namespace MudMaps.City.Pages;

public partial class Home : MapPageBase
{
    private const double C = 0.8660254;
    private const double S = 0.5;
    private const double MaxSize = 4000;

    // Districts: origin of the iso plate (its back corner) and the size of one lot.
    private static readonly Dictionary<string, (double Ox, double Oy, double Cell)> Districts = new()
    {
        ["MudBlazor"] = (360, 250, 60),
        ["MudBlazor.Extensions"] = (990, 230, 60),
        ["CodeBeam Extensions"] = (1390, 360, 52),
        ["MudX"] = (140, 700, 44),
        ["TryMudBlazor"] = (390, 700, 44),
        ["Heron Calendar"] = (640, 700, 44),
        ["Templates"] = (880, 710, 44),
        ["Static Inputs"] = (1120, 710, 44),
        ["ThemeManager"] = (1380, 720, 44),
    };

    private sealed record Plate(string Repo, string Points, double LabelX, double LabelY, bool Big, int Prs);
    private sealed record Window(string Points, string Fill);
    private sealed record Building(Hood Hood, bool Cool, string Front, string Side, string Top, string RoofFill,
        List<Window> Windows, double LabelX, double LabelY, (double X, double Y, double Rx, double Ry) Focus, double Depth);

    private readonly List<Plate> _plates = [];
    private readonly List<Building> _buildings = [];
    private readonly Dictionary<string, Building> _byKey = [];

    protected override string RootLabel => "Whole city";

    protected override (double X, double Y, double Rx, double Ry) Focus(Hood hood) => _byKey[hood.Key].Focus;

    private static string HeatVar(int level) => $"var(--heat-{level})";

    private static (double X, double Y) Iso(double ox, double oy, double x, double y, double z) =>
        (ox + (x - y) * C, oy + (x + y) * S - z);

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

    protected override void Build(MapData data)
    {
        _plates.Clear();
        _buildings.Clear();
        _byKey.Clear();
        foreach (var repo in data.Repos ?? [])
        {
            if (!Districts.TryGetValue(repo.Name, out var d)) continue;
            var hoods = data.HoodsOf(repo.Name);
            var cols = (int)Math.Ceiling(Math.Sqrt(hoods.Count));
            var rows = (int)Math.Ceiling(hoods.Count / (double)cols);
            var w = cols * d.Cell + 12;
            var dep = rows * d.Cell + 12;
            var label = Iso(d.Ox, d.Oy, w / 2, dep, 0);
            _plates.Add(new Plate(repo.Name, Poly(d.Ox, d.Oy, (0, 0, 0), (w, 0, 0), (w, dep, 0), (0, dep, 0)),
                label.X, label.Y + 40, repo.Nodes > 10000, repo.Prs));

            var cool = repo.Name != "MudBlazor";
            var placed = hoods.Select((h, i) => (h, cx: i % cols, cy: i / cols)).OrderBy(t => t.cx + t.cy);
            foreach (var (h, cx, cy) in placed)
            {
                var f = Math.Min(1.15, Math.Sqrt(h.Size / MaxSize));
                var fw = Math.Min(d.Cell - 10, 18 + 30 * f);
                var ht = 24 + 170 * f;
                var x = 12 + cx * d.Cell + (d.Cell - fw) / 2;
                var y = 12 + cy * d.Cell + (d.Cell - fw) / 2;
                _buildings.Add(MakeBuilding(h, cool, d.Ox, d.Oy, x, y, fw, ht));
            }
        }
        foreach (var b in _buildings) _byKey[b.Hood.Key] = b;
    }

    private static Building MakeBuilding(Hood h, bool cool, double ox, double oy, double x, double y, double w, double ht)
    {
        var level = MapData.Heat(h.Prs);
        var front = Poly(ox, oy, (x, y + w, 0), (x + w, y + w, 0), (x + w, y + w, ht), (x, y + w, ht));
        var side = Poly(ox, oy, (x + w, y, 0), (x + w, y + w, 0), (x + w, y + w, ht), (x + w, y, ht));
        var top = Poly(ox, oy, (x, y, ht), (x + w, y, ht), (x + w, y + w, ht), (x, y + w, ht));

        // Windows on both faces; the lit share follows the heat.
        var windows = new List<Window>();
        var share = level == 0 ? 0 : Math.Min(1, .18 + level * .14);
        var rows = Math.Max(1, (int)((ht - 8) / 13));
        var cols = Math.Max(1, (int)(w / 11));
        var i = 0;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                i++;
                var lit = (i * 53 % 97) / 97.0 < share;
                var fill = lit ? HeatVar(Math.Max(2, level - (i * 7 % 3 == 0 ? 1 : 0))) : "var(--heat-0)";
                var cw = (w - 6) / cols;
                var wz = 7 + r * 13;
                var wx = x + 3 + c * cw;
                windows.Add(new Window(Poly(ox, oy, (wx + 1, y + w, wz), (wx + cw - 2, y + w, wz), (wx + cw - 2, y + w, wz + 7), (wx + 1, y + w, wz + 7)), fill));
                var wy = y + 3 + c * cw;
                windows.Add(new Window(Poly(ox, oy, (x + w, wy + 1, wz), (x + w, wy + cw - 2, wz), (x + w, wy + cw - 2, wz + 7), (x + w, wy + 1, wz + 7)),
                    lit ? HeatVar(Math.Max(2, level - 1)) : "var(--heat-0)"));
            }
        }

        var roof = Iso(ox, oy, x + w / 2, y + w / 2, ht);
        var mid = Iso(ox, oy, x + w / 2, y + w / 2, ht / 2);
        var rx = Math.Max(Math.Max(w * C * 1.4, 34), (ht / 2 + 14) / .62);
        var ry = rx * .62;
        var roofFill = level > 0 ? HeatVar(level) : cool ? "var(--stone-x3)" : "var(--stone-3)";
        return new Building(h, cool, front, side, top, roofFill, windows, roof.X, roof.Y - 10, (mid.X, mid.Y, rx, ry), x + y);
    }
}
