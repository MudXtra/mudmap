using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MudMaps.Shared.Research;

/// <summary>
/// The search bar's repo scope. It follows the map: zoomed into a repo it narrows to that repo, and on the whole map it
/// covers every repo. One click widens it back to all repos; the next zoom into a different repo narrows it again.
/// </summary>
public sealed class SearchScope
{
    private string? _zoomed;

    /// <summary>The reader widened the scope while zoomed in.</summary>
    public bool Widened { get; private set; }

    /// <summary>The repo searches are limited to (full name), or null for every repo.</summary>
    public string? Repo => Widened ? null : _zoomed;

    /// <summary>The map is now zoomed into <paramref name="repo"/> (full name), or out to the whole map when null.</summary>
    /// <returns>True when <see cref="Repo"/> changed.</returns>
    public bool Follow(string? repo)
    {
        if (repo == _zoomed) return false;
        var before = Repo;
        _zoomed = repo;
        Widened = false;
        return Repo != before;
    }

    /// <summary>Search every repo until the map moves to another repo.</summary>
    public void Widen() => Widened = true;

    /// <summary>The repo the map is zoomed into, even when the scope is widened.</summary>
    public string? Zoomed => _zoomed;
}

/// <summary>Finds open pull requests by number, title, author, repo or label.</summary>
public static class PrMatcher
{
    /// <summary>
    /// The pull requests matching every word of <paramref name="query"/>: a number (with or without #) matches the start of
    /// the PR number; other words match the title, author, repo full name, repo display name or a label. Best first: an
    /// exact number, then a number prefix, then a title that starts with the query, then the rest by most recent.
    /// </summary>
    /// <param name="repoName">The display name of a repo full name, so "CodeBeam Extensions" matches too.</param>
    public static IReadOnlyList<PrItem> Match(IEnumerable<PrItem> prs, string? query, Func<string, string?>? repoName = null, int max = 8)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.TrimStart('#')).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return prs.OrderBy(p => p.AgeDays).Take(max).ToList();
        var whole = string.Join(' ', words);
        return prs
            .Select(p => (Pr: p, Rank: Rank(p, words, whole, repoName?.Invoke(p.Repo))))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank).ThenBy(x => x.Pr.AgeDays).ThenByDescending(x => x.Pr.Number)
            .Take(max).Select(x => x.Pr).ToList();
    }

    private static int Rank(PrItem p, List<string> words, string whole, string? name)
    {
        var number = p.Number.ToString(CultureInfo.InvariantCulture);
        foreach (var w in words)
        {
            var hit = number.StartsWith(w, StringComparison.Ordinal)
                || Has(p.Title, w) || Has(p.Author, w) || Has(p.Repo, w) || Has(name, w) || p.LabelList.Any(l => Has(l, w));
            if (!hit) return -1;
        }
        if (words.Count == 1 && number == words[0]) return 0;
        if (words.Count == 1 && number.StartsWith(words[0], StringComparison.Ordinal)) return 1;
        if (p.Title.StartsWith(whole, StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static bool Has(string? text, string word) => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>A column of the pull request review board.</summary>
public enum BoardLane { Conflict, Waiting, Ready, Other }

/// <summary>Sorts open pull requests into the review board's columns.</summary>
public static class BoardGrouping
{
    /// <summary>
    /// Conflict risk when another open PR touches the same rooms; otherwise ready to merge (READY, APPROVED), waiting on
    /// review (PENDING), or other (CI-FAIL, CHANGES-REQ, DRAFT, STALE, WRONG-BASE and anything new).
    /// </summary>
    public static BoardLane Lane(PrItem pr) =>
        pr.Conflicts.Count > 0 || pr.Mergeable == "CONFLICTING" ? BoardLane.Conflict
        : pr.Status is "READY" or "APPROVED" ? BoardLane.Ready
        : pr.Status is "PENDING" ? BoardLane.Waiting
        : BoardLane.Other;

    public static string Title(BoardLane lane) => lane switch
    {
        BoardLane.Conflict => "Conflict risk",
        BoardLane.Waiting => "Waiting on review",
        BoardLane.Ready => "Ready to merge",
        _ => "Other",
    };

    /// <summary>Every column in board order with its PRs, largest blast radius first within a column.</summary>
    /// <param name="repos">Full names to keep; empty keeps every repo.</param>
    /// <param name="actionable">Keep only PRs against the default branch that are not stale (the triage filter).</param>
    public static IReadOnlyList<(BoardLane Lane, IReadOnlyList<PrItem> Prs)> Group(IEnumerable<PrItem> prs,
        IReadOnlyCollection<string> repos, bool actionable)
    {
        var kept = prs.Where(p => repos.Count == 0 || repos.Contains(p.Repo))
            .Where(p => !actionable || IsActionable(p)).ToList();
        return Enum.GetValues<BoardLane>()
            .Select(l => (l, (IReadOnlyList<PrItem>)kept.Where(p => Lane(p) == l)
                .OrderByDescending(p => p.Nodes).ThenBy(p => p.AgeDays).ToList()))
            .ToList();
    }

    /// <summary>The triage filter: against the default branch and not stale or wrong-base.</summary>
    public static bool IsActionable(PrItem p) =>
        p.Status is not ("STALE" or "WRONG-BASE") && (p.DefaultBase is null || p.Base is null || p.Base == p.DefaultBase);

    /// <summary>Why a card sits in conflict risk: the rooms it shares and with which PRs.</summary>
    public static string? ConflictReason(PrItem p)
    {
        if (p.Conflicts.Count == 0)
        {
            return p.Mergeable == "CONFLICTING" ? "Merge conflicts with the base branch" : null;
        }
        var others = $"#{string.Join(", #", p.Conflicts.Select(c => c.Number))}";
        var files = p.Conflicts.SelectMany(c => c.Files ?? []).Distinct().ToList();
        return files.Count switch
        {
            0 => $"Changes the same files as {others}",
            1 => $"Changes {files[0].Split('/')[^1]} like {others}",
            _ => $"Changes {files.Count} of the same files as {others}",
        };
    }
}

/// <summary>Which repos the review board shows: its own picker, or the map's repo when "Follow map" is on.</summary>
public static class BoardRepos
{
    /// <param name="picked">Full names picked in the board's picker; empty means all repos.</param>
    /// <param name="zoomed">The repo the map is zoomed into (full name), or null on the whole map.</param>
    /// <returns>Full names to show; empty means all repos.</returns>
    public static IReadOnlySet<string> Effective(bool followMap, IReadOnlySet<string> picked, string? zoomed) =>
        !followMap ? picked : zoomed is null ? new HashSet<string>() : new HashSet<string> { zoomed };
}

/// <summary>A room two or more open PRs both touch, ringed on the map and labelled "#a + #b".</summary>
public sealed record ConflictRoom(string RoomId, IReadOnlyList<int> Numbers)
{
    /// <summary>"#a + #b" for a pair; a count once more PRs share the room, so a busy room's label stays short.</summary>
    public string Label => Numbers.Count <= 2 ? "#" + string.Join(" + #", Numbers) : $"{Numbers.Count} PRs";
}

/// <summary>Where open PRs collide on the map.</summary>
public static class ConflictMap
{
    /// <summary>The shared rooms in one repo, each with the PRs that share it (numbers ascending), busiest room first.</summary>
    public static IReadOnlyList<ConflictRoom> Rooms(IEnumerable<PrItem> prs, string repo)
    {
        var byRoom = new Dictionary<string, SortedSet<int>>();
        foreach (var p in prs.Where(p => p.Repo == repo))
        {
            foreach (var c in p.Conflicts)
            {
                foreach (var room in c.Rooms)
                {
                    if (!byRoom.TryGetValue(room, out var set)) byRoom[room] = set = [];
                    set.Add(p.Number);
                    set.Add(c.Number);
                }
            }
        }
        return byRoom.Select(kv => new ConflictRoom(kv.Key, kv.Value.ToList()))
            .OrderByDescending(r => r.Numbers.Count).ThenBy(r => r.RoomId, StringComparer.Ordinal).ToList();
    }

    /// <summary>How many conflict-risk PRs each repo has (full name to count), for the whole-map badges.</summary>
    public static IReadOnlyDictionary<string, int> CountByRepo(IEnumerable<PrItem> prs) =>
        prs.Where(p => p.Conflicts.Count > 0).GroupBy(p => p.Repo).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>
    /// The PRs in conflict in one repo with the rooms each touches that it shares with another PR: one pin per PR, joined
    /// to those rooms by dashed lines.
    /// </summary>
    /// <param name="focus">Only this PR's pin is drawn; none when no card is pointed at or open.</param>
    public static IReadOnlyList<(int Number, IReadOnlyList<string> Rooms)> Pins(IEnumerable<PrItem> prs, string repo, int? focus) =>
        prs.Where(p => p.Repo == repo && p.Conflicts.Count > 0 && p.Number == focus)
            .Select(p => (p.Number, (IReadOnlyList<string>)p.Conflicts.SelectMany(c => c.Rooms).Distinct().Order(StringComparer.Ordinal).ToList()))
            .OrderBy(x => x.Number).ToList();

    /// <summary>
    /// Spreads pins that would sit on the same spot: pins whose anchors are closer than <paramref name="minGap"/> are fanned
    /// out on a small circle around their shared centre, so no two labels sit on top of each other.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Spread(IReadOnlyList<(double X, double Y)> anchors, double minGap)
    {
        var result = anchors.ToArray();
        var done = new bool[anchors.Count];
        for (var i = 0; i < anchors.Count; i++)
        {
            if (done[i]) continue;
            var group = Enumerable.Range(i, anchors.Count - i)
                .Where(j => !done[j] && Math.Abs(anchors[j].X - anchors[i].X) < minGap && Math.Abs(anchors[j].Y - anchors[i].Y) < minGap).ToList();
            foreach (var j in group) done[j] = true;
            if (group.Count < 2) continue;
            var cx = group.Average(j => anchors[j].X);
            var cy = group.Average(j => anchors[j].Y);
            var radius = minGap * group.Count / (2 * Math.PI) + minGap / 2;
            for (var k = 0; k < group.Count; k++)
            {
                var a = -Math.PI / 2 + k * 2 * Math.PI / group.Count;
                result[group[k]] = (cx + Math.Cos(a) * radius, cy + Math.Sin(a) * radius);
            }
        }
        return result;
    }
}

/// <summary>A search or research result laid over the map: rooms lit, the rest dimmed, counts per area and repo.</summary>
public sealed class MapLens
{
    private readonly Dictionary<string, int> _rooms;
    private readonly Dictionary<string, int> _hoods = [];
    private readonly Dictionary<string, int> _repos = [];

    /// <param name="label">What the lens shows, for the line above the map.</param>
    /// <param name="rooms">The rooms to light, with their item counts.</param>
    public MapLens(string label, IEnumerable<LensRoom> rooms, MapData data)
    {
        Label = label;
        _rooms = rooms.GroupBy(r => r.RoomId).ToDictionary(g => g.Key, g => g.Sum(r => Math.Max(1, r.Count)));
        var hoodOf = data.Clusters.ToDictionary(c => c.Id, c => c.Hood);
        var repoOf = data.Hoods.ToDictionary(h => h.Key, h => h.Repo);
        foreach (var (id, n) in _rooms)
        {
            if (!hoodOf.TryGetValue(id, out var hood)) continue;
            RoomCount++;
            _hoods[hood] = _hoods.GetValueOrDefault(hood) + n;
            if (repoOf.TryGetValue(hood, out var repo)) _repos[repo] = _repos.GetValueOrDefault(repo) + 1;
        }
    }

    public string Label { get; }

    /// <summary>Rooms on the map the lens lights.</summary>
    public int RoomCount { get; }

    public bool Lit(Cluster room) => _rooms.ContainsKey(room.Id);
    public bool Lit(Hood hood) => _hoods.ContainsKey(hood.Key);

    /// <summary>Matching rooms in a repo (by display name), for the count badge on the whole map.</summary>
    public int Count(string repoName) => _repos.GetValueOrDefault(repoName);

    /// <summary>Display names of the repos with matches.</summary>
    public IReadOnlyCollection<string> Repos => _repos.Keys;

    /// <summary>The single room the lens lights, when there is exactly one on the map.</summary>
    public string? OnlyRoom => _rooms.Count == 1 ? _rooms.Keys.First() : null;

    public bool HasRoom(string roomId) => _rooms.ContainsKey(roomId);
}

/// <summary>The figures graphify's <c>graph_stats</c> reports, read from its text.</summary>
public sealed record GraphFigures(int Nodes, int Edges, int Communities)
{
    public static GraphFigures? Parse(string? text)
    {
        if (text is null) return null;
        int? Read(string name) => Regex.Match(text, $@"^{name}:\s*(\d+)", RegexOptions.Multiline) is { Success: true } m
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        return Read("Nodes") is { } n && Read("Edges") is { } e && Read("Communities") is { } c ? new(n, e, c) : null;
    }
}

/// <summary>Rooms are graphify communities: a room id is <c>{repo slug}:{community id}</c>.</summary>
public static class RoomIds
{
    public static int? Community(string roomId)
    {
        var i = roomId.LastIndexOf(':');
        return i >= 0 && int.TryParse(roomId.AsSpan(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}

/// <summary>The MCP JSON-RPC request equivalent to a research call, for "Copy as MCP call" before a run.</summary>
public static class McpCall
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Build(string tool, IReadOnlyDictionary<string, object?> arguments) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new Dictionary<string, object> { ["name"] = tool, ["arguments"] = arguments },
        }, Indented);

    public static string Format(JsonElement mcp) => JsonSerializer.Serialize(mcp, Indented);
}
