using System.Text.Json;
using System.Text.RegularExpressions;

namespace MudMaps.Server.Maps;

/// <summary>
/// The part of the map that depends only on one repository's Graphify graph: areas, rooms, roads
/// and the file-to-room index used to place pull requests. Built once per graph file version.
/// </summary>
public sealed partial class RepoGraph
{
    public required string FullName { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public required string? BuiltAtCommit { get; init; }
    public required int Nodes { get; init; }
    public required IReadOnlyList<(string Key, string Area, int Size, int TestSize)> Areas { get; init; }
    public required IReadOnlyList<RoomInfo> Rooms { get; init; }
    public required IReadOnlyDictionary<(string, string), int> Roads { get; init; }
    public required IReadOnlyDictionary<string, string> TypeIndex { get; init; }
    public required IReadOnlyList<(string HoodKey, string Label)> ExternalRefs { get; init; }
    public required IReadOnlyDictionary<string, List<string>> FileRooms { get; init; }
    public required IReadOnlyDictionary<string, string> CanonicalArea { get; init; }
    public required IReadOnlySet<string> KeptAreas { get; init; }
    public required bool HasOther { get; init; }

    /// <param name="TestN">How many of the room's items are test code.</param>
    /// <param name="Path">The room's most common folder, repository-relative, for linking to GitHub.</param>
    public sealed record RoomInfo(string Id, string Hood, int N, string Dir, IReadOnlyList<string> Top, string Name, int TestN = 0, string Path = "");

    public string OtherKey => $"{Slug}/other";

    public string? HoodKeyFor(string? area)
    {
        if (area is null) return null;
        if (CanonicalArea.TryGetValue(area.ToLowerInvariant(), out var canon)) area = canon;
        if (KeptAreas.Contains(area)) return $"{Slug}/{area.ToLowerInvariant()}";
        return HasOther ? OtherKey : null;
    }

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]+$")]
    private static partial Regex TypeName();

    [GeneratedRegex(@"^Mud[A-Z]\w+$")]
    private static partial Regex MudType();

    private sealed record Node(string Id, string Label, string? File, int? Community);

    /// <summary>Read a graph.json and derive the map structure for one repository.</summary>
    public static RepoGraph Load(string fullName, string name, int budget, Stream graphJson, bool indexMudTypes, string? licenseFile = null)
    {
        using var doc = JsonDocument.Parse(graphJson, new JsonDocumentOptions { MaxDepth = 64 });
        var root = doc.RootElement;
        var nodes = new Dictionary<string, Node>();
        foreach (var n in root.GetProperty("nodes").EnumerateArray())
        {
            var id = n.GetProperty("id").GetString()!;
            nodes[id] = new Node(id,
                n.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                n.TryGetProperty("source_file", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() is { Length: > 0 } file ? file : null,
                n.TryGetProperty("community", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null);
        }
        var built = root.TryGetProperty("built_at_commit", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;

        var canon = new Dictionary<string, string>();
        var nodeArea = new Dictionary<string, string>();
        var size = new Dictionary<string, int>();
        var testSize = new Dictionary<string, int>();
        var isTest = new HashSet<string>();
        foreach (var n in nodes.Values)
        {
            var a = AreaRules.AreaOf(n.File);
            if (a is null) continue;
            if (!canon.TryGetValue(a.ToLowerInvariant(), out var c)) canon[a.ToLowerInvariant()] = c = a;
            nodeArea[n.Id] = c;
            size[c] = size.GetValueOrDefault(c) + 1;
            if (AreaRules.IsTest(n.File))
            {
                isTest.Add(n.Id);
                testSize[c] = testSize.GetValueOrDefault(c) + 1;
            }
        }

        var keep = size.OrderByDescending(kv => kv.Value).Take(budget).Select(kv => kv.Key).ToList();
        var keepSet = keep.ToHashSet();
        var hasOther = size.Count > budget;
        var slug = AreaRules.Slug(name);
        string? HoodKey(string? area) => area is null ? null : keepSet.Contains(area) ? $"{slug}/{area.ToLowerInvariant()}" : hasOther ? $"{slug}/other" : null;

        var degree = new Dictionary<string, int>();
        var roads = new Dictionary<(string, string), int>();
        var external = new List<(string, string)>();
        foreach (var link in root.GetProperty("links").EnumerateArray())
        {
            var s = link.GetProperty("source").GetString()!;
            var t = link.GetProperty("target").GetString()!;
            degree[s] = degree.GetValueOrDefault(s) + 1;
            degree[t] = degree.GetValueOrDefault(t) + 1;
            var ha = HoodKey(nodeArea.GetValueOrDefault(s));
            var hb = HoodKey(nodeArea.GetValueOrDefault(t));
            if (ha is not null && hb is not null && ha != hb)
            {
                var key = string.CompareOrdinal(ha, hb) < 0 ? (ha, hb) : (hb, ha);
                roads[key] = roads.GetValueOrDefault(key) + 1;
            }
            foreach (var (mine, theirs) in new[] { (s, t), (t, s) })
            {
                if (nodeArea.TryGetValue(mine, out var ma) && !nodeArea.ContainsKey(theirs)
                    && nodes.TryGetValue(theirs, out var other) && other.File is null && HoodKey(ma) is { } hk)
                {
                    external.Add((hk, other.Label));
                }
            }
        }

        // Rooms: communities, assigned to the area most of their nodes live in.
        var byCommunity = new Dictionary<int, List<string>>();
        foreach (var (id, _) in nodeArea)
        {
            if (nodes[id].Community is not { } cid) continue;
            if (!byCommunity.TryGetValue(cid, out var list)) byCommunity[cid] = list = [];
            list.Add(id);
        }
        var roomsByHood = new Dictionary<string, List<RoomInfo>>();
        var memberFiles = new Dictionary<string, List<string>>();
        foreach (var (cid, members) in byCommunity)
        {
            if (members.Count < 3) continue;
            var hk = HoodKey(Mode(members.Select(m => nodeArea[m])));
            if (hk is null) continue;
            var labels = members.OrderByDescending(m => degree.GetValueOrDefault(m))
                .Select(m => nodes[m].Label).Where(l => l.Length > 0).Distinct().Take(8).ToList();
            var dir = Mode(members.Select(m => DirOf(nodes[m].File!)));
            var stem = Mode(members.Select(m => nodes[m].File!.Split('/')[^1].Split('.')[0]));
            var typed = labels.FirstOrDefault(l => TypeName().IsMatch(l) && !AreaRules.Generic.Contains(l));
            var roomName = TypeName().IsMatch(stem) ? stem : typed ?? (stem.Length > 0 ? stem : $"Cluster {cid}");
            var path = Mode(members.Select(m => nodes[m].File!.Contains('/') ? nodes[m].File![..nodes[m].File!.LastIndexOf('/')] : ""));
            var room = new RoomInfo($"{slug}:{cid}", hk, members.Count, dir, labels, roomName, members.Count(isTest.Contains), path);
            if (!roomsByHood.TryGetValue(hk, out var rs)) roomsByHood[hk] = rs = [];
            rs.Add(room);
            memberFiles[room.Id] = members.Select(m => nodes[m].File!).Distinct().ToList();
        }

        var rooms = new List<RoomInfo>();
        foreach (var (_, rs) in roomsByHood)
        {
            var ordered = rs.OrderByDescending(r => r.N).ToList();
            var dupes = ordered.GroupBy(r => r.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var used = new HashSet<string>();
            for (var i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i];
                if (dupes.Contains(r.Name))
                {
                    var alt = r.Top.Select(l => l.Split('.')[^1].TrimEnd('(', ')'))
                        .FirstOrDefault(a => a.Length > 0 && char.IsUpper(a[0]) && !AreaRules.Generic.Contains(a) && a != r.Name && !used.Contains($"{r.Name} · {a}"));
                    if (alt is not null) ordered[i] = r = r with { Name = $"{r.Name} · {alt}" };
                }
                used.Add(r.Name);
            }
            rooms.AddRange(ordered.Take(16));
        }

        var fileRooms = new Dictionary<string, List<string>>();
        foreach (var r in rooms)
        {
            foreach (var f in memberFiles[r.Id])
            {
                if (!fileRooms.TryGetValue(f, out var list)) fileRooms[f] = list = [];
                list.Add(r.Id);
            }
        }

        var typeIndex = new Dictionary<string, string>();
        if (indexMudTypes)
        {
            foreach (var (id, area) in nodeArea)
            {
                var label = nodes[id].Label;
                if (MudType().IsMatch(label) && HoodKey(area) is { } hk) typeIndex.TryAdd(label, hk);
            }
        }

        return new RepoGraph
        {
            FullName = fullName,
            Name = name,
            Slug = slug,
            BuiltAtCommit = built,
            Nodes = size.Values.Sum(),
            Areas = keep.Select(a => ($"{slug}/{a.ToLowerInvariant()}", a, size[a], testSize.GetValueOrDefault(a))).ToList(),
            Rooms = rooms,
            Roads = roads,
            TypeIndex = typeIndex,
            ExternalRefs = external,
            FileRooms = fileRooms,
            CanonicalArea = canon,
            KeptAreas = keepSet,
            HasOther = hasOther,
            OtherSize = hasOther ? size.Where(kv => !keepSet.Contains(kv.Key)).Sum(kv => kv.Value) : 0,
            OtherTestSize = hasOther ? testSize.Where(kv => !keepSet.Contains(kv.Key)).Sum(kv => kv.Value) : 0,
            TestNodes = isTest.Count,
            LicenseFile = licenseFile,
        };
    }

    public required int OtherSize { get; init; }
    public required int OtherTestSize { get; init; }
    public required int TestNodes { get; init; }

    /// <summary>The license file's name at the repository root ("LICENSE", "LICENSE.txt"), if there is one.</summary>
    public string? LicenseFile { get; init; }

    /// <summary>The last two folders of a path ("Components/Chart"), or fewer for shallow paths.</summary>
    private static string DirOf(string file)
    {
        var parts = file.Split('/');
        return parts.Length < 2 ? "" : string.Join('/', parts[Math.Max(0, parts.Length - 3)..^1]);
    }

    private static string Mode(IEnumerable<string> values) =>
        values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
}
