using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudMaps.Shared;

namespace MudMaps.Stars.Pages;

public partial class Home : MapPageBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private static readonly Dictionary<string, (double X, double Y)> Systems = new()
    {
        ["MudBlazor"] = (430, 470),
        ["MudBlazor.Extensions"] = (1130, 360),
        ["CodeBeam Extensions"] = (1330, 760),
        ["MudX"] = (120, 880),
        ["TryMudBlazor"] = (330, 880),
        ["Heron Calendar"] = (560, 880),
        ["Templates"] = (790, 880),
        ["Static Inputs"] = (1010, 880),
        ["ThemeManager"] = (1530, 920),
    };

    private sealed record Star(RepoInfo Repo, double X, double Y, double R, string Fill, double LabelY);
    private sealed record Planet(Hood Hood, double Cx, double Cy, double Orbit, double Angle, double Speed, double R, int Moons, bool Ring);
    private sealed record Dot(double X, double Y, double R, bool Twinkle, double Delay);
    private sealed record Arc(string Path);

    private readonly List<Star> _stars = [];
    private readonly List<Planet> _planets = [];
    private readonly List<Dot> _dots = [];
    private readonly List<Arc> _arcs = [];
    private readonly Dictionary<string, Planet> _byKey = [];
    private IJSObjectReference? _module;

    protected override string RootLabel => "Whole sky";
    protected override (string Prs, string Merged, string Repos) FigureLabels => ("moons", "merged", "star systems");

    protected override (double X, double Y, double Rx, double Ry) Focus(Hood hood)
    {
        var p = _byKey[hood.Key];
        return (p.Cx + Math.Cos(p.Angle) * p.Orbit, p.Cy + Math.Sin(p.Angle) * p.Orbit * .5, 80, 50);
    }

    protected override async Task<(double X, double Y)?> LiveCentreAsync(Hood hood)
    {
        if (_module is null) return null;
        var at = await _module.InvokeAsync<double[]?>("hold", Svg, hood.Key);
        return at is { Length: 2 } ? (at[0], at[1]) : null;
    }

    protected override async Task OnMapRenderedAsync()
    {
        _module = await JS.InvokeAsync<IJSObjectReference>("import", $"./stars.js?v={typeof(Home).Assembly.ManifestModule.ModuleVersionId:N}");
        await _module.InvokeVoidAsync("start", Svg);
    }

    protected override async Task OnMapUpdatedAsync()
    {
        if (_module is not null) await _module.InvokeVoidAsync("start", Svg);
    }

    protected override async Task HomeAsync()
    {
        if (_module is not null) await _module.InvokeVoidAsync("release", Svg);
        await base.HomeAsync();
    }

    protected override void Build(MapData data)
    {
        _stars.Clear();
        _planets.Clear();
        _dots.Clear();
        _arcs.Clear();
        _byKey.Clear();
        var rnd = new Random(11);
        for (var i = 0; i < 320; i++)
        {
            _dots.Add(new Dot(rnd.NextDouble() * ViewWidth, rnd.NextDouble() * ViewHeight, rnd.NextDouble() < .08 ? 1.4 : .7, i % 5 == 0, rnd.NextDouble() * 4));
        }

        foreach (var repo in data.Repos ?? [])
        {
            if (!Systems.TryGetValue(repo.Name, out var c)) continue;
            var hoods = data.HoodsOf(repo.Name);
            var starR = 6 + Math.Sqrt(repo.Nodes) * .15;
            var step = hoods.Count > 8 ? Math.Min(14, 320.0 / hoods.Count) : 11;
            var baseOrbit = starR + 14;
            var fill = repo.Name == "MudBlazor" ? "var(--gold)" : repo.Prs > 0 ? "var(--purple)" : "var(--silver)";
            var outer = baseOrbit + hoods.Count * step;
            _stars.Add(new Star(repo, c.X, c.Y, starR, fill, c.Y + outer * .5 + 22));
            for (var i = 0; i < hoods.Count; i++)
            {
                var h = hoods[i];
                var orbit = baseOrbit + (i + 1) * step;
                var p = new Planet(h, c.X, c.Y, orbit, i * 2.4, 6 / orbit, 3.5 + Math.Sqrt(h.Size) * .17,
                    Math.Min(8, (int)Math.Ceiling(h.Prs / 4.0)), h.Prs >= 20);
                _planets.Add(p);
                _byKey[h.Key] = p;
            }
        }

        // The strongest pulls between systems, drawn star to star.
        var starOf = _stars.ToDictionary(s => s.Repo.Name);
        var seen = new HashSet<(string, string)>();
        foreach (var r in data.Roads)
        {
            if (!_byKey.TryGetValue(r.A, out var a) || !_byKey.TryGetValue(r.B, out var b) || a.Hood.Repo == b.Hood.Repo) continue;
            var pair = string.CompareOrdinal(a.Hood.Repo, b.Hood.Repo) < 0 ? (a.Hood.Repo, b.Hood.Repo) : (b.Hood.Repo, a.Hood.Repo);
            if (!seen.Add(pair)) continue;
            var sa = starOf[pair.Item1];
            var sb = starOf[pair.Item2];
            _arcs.Add(new Arc($"M{F(sa.X)},{F(sa.Y)} Q{F((sa.X + sb.X) / 2)},{F(Math.Min(sa.Y, sb.Y) - 140)} {F(sb.X)},{F(sb.Y)}"));
            if (_arcs.Count == 6) break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
