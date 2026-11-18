namespace MudMaps.Shared;

/// <summary>A circle placed by <see cref="ForceLayout"/>.</summary>
public sealed class LayoutNode(string key, string group, double radius)
{
    public string Key { get; } = key;
    public string Group { get; } = group;
    public double R { get; } = radius;
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>Small deterministic layout: pull to a group centre, attract along roads, push apart on overlap.</summary>
public static class ForceLayout
{
    /// <param name="squash">Vertical squash for elliptical shapes (1 = circles).</param>
    public static void Relax(
        IReadOnlyList<LayoutNode> nodes,
        IReadOnlyDictionary<string, (double X, double Y)> centres,
        IEnumerable<Road> roads,
        (double MinX, double MinY, double MaxX, double MaxY) bounds,
        double gap = 16,
        double squash = 1,
        int iterations = 400,
        int seed = 7,
        double pull = 0.02)
    {
        var rnd = new Random(seed);
        for (var i = 0; i < nodes.Count; i++)
        {
            var c = centres[nodes[i].Group];
            nodes[i].X = c.X + Math.Cos(i * 2.4) * 120 + rnd.NextDouble();
            nodes[i].Y = c.Y + Math.Sin(i * 2.4) * 110 + rnd.NextDouble();
        }

        var byKey = nodes.ToDictionary(n => n.Key);
        var links = roads.Where(r => byKey.ContainsKey(r.A) && byKey.ContainsKey(r.B)).ToList();
        var maxW = links.Count > 0 ? links.Max(r => r.W) : 1;

        for (var it = 0; it < iterations; it++)
        {
            foreach (var n in nodes)
            {
                var c = centres[n.Group];
                n.X += (c.X - n.X) * pull;
                n.Y += (c.Y - n.Y) * pull;
            }

            foreach (var r in links)
            {
                var a = byKey[r.A];
                var b = byKey[r.B];
                var k = 0.0015 * r.W / maxW;
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                a.X += dx * k; a.Y += dy * k;
                b.X -= dx * k; b.Y -= dy * k;
            }

            for (var i = 0; i < nodes.Count; i++)
            {
                for (var j = i + 1; j < nodes.Count; j++)
                {
                    var a = nodes[i];
                    var b = nodes[j];
                    var dx = b.X - a.X;
                    var dy = (b.Y - a.Y) / squash;
                    var d = Math.Max(0.001, Math.Sqrt(dx * dx + dy * dy));
                    var min = a.R + b.R + gap;
                    if (d >= min) continue;
                    var push = (min - d) / 2;
                    dx /= d; dy /= d;
                    a.X -= dx * push; a.Y -= dy * push * squash;
                    b.X += dx * push; b.Y += dy * push * squash;
                }
            }

            foreach (var n in nodes)
            {
                n.X = Math.Clamp(n.X, bounds.MinX + n.R, bounds.MaxX - n.R);
                n.Y = Math.Clamp(n.Y, bounds.MinY + n.R * squash, bounds.MaxY - n.R * squash);
            }
        }
    }
}
