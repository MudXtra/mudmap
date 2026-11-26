using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MudMaps.Server.GitHub;
using MudMaps.Server.Maps;

namespace MudMaps.Server.Research;

/// <summary>One open pull request with graphify's classification and graph impact, for both the MCP text and the board.</summary>
public sealed record PrView(
    string Repo,
    OpenPr Pr,
    string DefaultBase,
    string ExpectedBase,
    string Status,
    int AgeDays,
    IReadOnlyList<int> Communities,
    int Nodes,
    IReadOnlyList<string> Rooms)
{
    /// <summary>graphify's <c>PRInfo.blast_radius</c>: "3 nodes / 1 community", or "" when nothing matched.</summary>
    public string BlastRadius => Nodes == 0 ? "" :
        $"{Nodes} node{(Nodes != 1 ? "s" : "")} / {Communities.Count} communit{(Communities.Count != 1 ? "ies" : "y")}";
}

/// <summary>
/// graphify's three PR tools (<c>list_prs</c>, <c>get_pr_impact</c>, <c>triage_prs</c>) answered from the Mud Maps
/// cache only. Depends on the cache snapshot and the loaded graphs, never
/// on a GitHub client, so no call can reach GitHub. Text mirrors graphify 0.9.108's prs.py/serve.py formats.
/// </summary>
public sealed class PrTools(IPrCacheSnapshot cache, IRepoGraphs graphs, IOptions<MapsOptions> maps, TimeProvider clock)
{
    /// <summary>graphify's <c>_STATUS_ORDER</c>.</summary>
    public static readonly string[] StatusOrder = ["WRONG-BASE", "CI-FAIL", "CHANGES-REQ", "DRAFT", "STALE", "PENDING", "APPROVED", "READY"];
    public const int StaleDays = 14;

    public static string NotCached(int number) => $"PR #{number} is not in the Mud Maps cache (open PRs, refreshed every 10 min).";

    private IEnumerable<string> AllRepos => maps.Value.Repos.Select(r => r.FullName);

    /// <summary>graphify's <c>_classify</c>.</summary>
    public static string Classify(OpenPr pr, string expectedBase, int ageDays)
    {
        if (pr.BaseRef != expectedBase) return "WRONG-BASE";
        if (pr.Ci == "FAILURE") return "CI-FAIL";
        if (pr.ReviewDecision == "CHANGES_REQUESTED") return "CHANGES-REQ";
        if (pr.IsDraft) return "DRAFT";
        if (ageDays >= StaleDays) return "STALE";
        if (pr.ReviewDecision == "APPROVED") return "APPROVED";
        if (pr.Ci == "PENDING") return "PENDING";
        return "READY";
    }

    /// <summary>graphify's <c>_path_match</c>: the same file, path-boundary safe.</summary>
    public static bool PathMatch(string graphSrc, string prFile) =>
        graphSrc == prFile || graphSrc.EndsWith("/" + prFile, StringComparison.Ordinal) || prFile.EndsWith("/" + graphSrc, StringComparison.Ordinal);

    /// <summary>graphify's <c>compute_pr_impact</c> against one repository's own graph, plus the map rooms of the matched files.</summary>
    public static (IReadOnlyList<int> Communities, int Nodes, IReadOnlyList<string> Rooms) Impact(IReadOnlyList<string> files, RepoGraph? graph)
    {
        if (graph is null || files.Count == 0) return ([], 0, []);
        var communities = new SortedSet<int>();
        var rooms = new List<string>();
        var nodes = 0;
        var matched = new HashSet<string>();
        foreach (var f in files)
        {
            foreach (var (src, impact) in graph.FileCommunities)
            {
                if (matched.Contains(src) || !PathMatch(src, f)) continue;
                matched.Add(src);
                communities.UnionWith(impact.Communities);
                nodes += impact.Nodes;
                foreach (var room in graph.FileRooms.GetValueOrDefault(src) ?? []) if (!rooms.Contains(room)) rooms.Add(room);
            }
        }
        return (communities.ToList(), nodes, rooms);
    }

    public PrView View(RepoOpenPrs repo, OpenPr pr, string? baseOverride = null)
    {
        var defaultBase = repo.DefaultBranch ?? "main";
        var expected = baseOverride ?? defaultBase;
        var age = (int)Math.Floor((clock.GetUtcNow() - pr.UpdatedAt).TotalDays);
        var (communities, nodes, rooms) = Impact(pr.Files, graphs.Graph(repo.FullName));
        return new PrView(repo.FullName, pr, defaultBase, expected, Classify(pr, expected, age), age, communities, nodes, rooms);
    }

    private static int Rank(string status) => Array.IndexOf(StatusOrder, status) is var i and >= 0 ? i : 99;

    private static string Review(OpenPr pr) => pr.ReviewDecision.Length > 0 ? pr.ReviewDecision : "none";

    private string Sections(string? repo, Func<RepoOpenPrs, string> body)
    {
        var repos = repo is null ? AllRepos.ToList() : [repo];
        var parts = new List<string>();
        foreach (var r in repos)
        {
            var text = cache.OpenPrs.Repos.TryGetValue(r, out var cached)
                ? body(cached)
                : $"Open PRs for {r} are not in the Mud Maps cache (open PRs, refreshed every 10 min).";
            parts.Add(repo is null ? $"== {r} ==\n{text}" : text);
        }
        return string.Join("\n\n", parts);
    }

    /// <summary>list_prs: graphify's <c>format_prs_text</c>, one section per repository when none is named.</summary>
    public string ListPrs(string? repo, string? baseBranch) => Sections(repo, r =>
    {
        var views = r.Prs.Select(p => View(r, p, baseBranch)).ToList();
        var b = baseBranch ?? r.DefaultBranch ?? "main";
        var actionable = views.Where(v => v.Pr.BaseRef == b).ToList();
        var lines = new List<string> { $"Open PRs targeting {b}: {actionable.Count}  ({views.Count - actionable.Count} on wrong base, not shown)\n" };
        foreach (var v in actionable.OrderBy(v => Rank(v.Status)).ThenBy(v => v.AgeDays))
        {
            var impact = v.BlastRadius.Length > 0 ? $"  blast_radius={v.BlastRadius}" : "";
            lines.Add($"#{v.Pr.Number} [{v.Status}] CI={v.Pr.Ci} review={Review(v.Pr)} age={v.AgeDays}d author={v.Pr.Author}{impact}\n  {v.Pr.Title}");
        }
        return string.Join("\n\n", lines);
    });

    /// <summary>triage_prs: graphify's <c>_tool_triage_prs</c> text, without the worktree field.</summary>
    public string TriagePrs(string? repo, string? baseBranch) => Sections(repo, r =>
    {
        var b = baseBranch ?? r.DefaultBranch ?? "main";
        var actionable = r.Prs.Select(p => View(r, p, baseBranch)).Where(v => v.Pr.BaseRef == b && v.Status is not ("WRONG-BASE" or "STALE")).ToList();
        if (actionable.Count == 0) return $"No actionable PRs targeting {b}.";
        var lines = new List<string>
        {
            $"Actionable PRs targeting {b}: {actionable.Count}\n" +
            "Rank these by review priority. Higher blast_radius = more graph communities affected = higher merge risk.\n",
        };
        foreach (var v in actionable.OrderBy(v => Rank(v.Status)))
        {
            var impact = v.BlastRadius.Length > 0 ? $"  blast_radius={v.BlastRadius}" : "";
            lines.Add($"PR #{v.Pr.Number} [{v.Status}] CI={v.Pr.Ci} review={Review(v.Pr)} age={v.AgeDays}d author={v.Pr.Author}{impact}\n  title: {v.Pr.Title}");
        }
        return string.Join("\n\n", lines);
    });

    /// <summary>get_pr_impact: graphify's <c>_tool_get_pr_impact</c> text.</summary>
    public string PrImpact(string repo, int number)
    {
        if (Find(repo, number) is not { } v) return NotCached(number);
        var files = v.Pr.Files;
        if (files.Count == 0) return $"PR #{number}: no changed files found (may require gh auth).";
        var lines = new List<string>
        {
            $"PR #{number}: {v.Pr.Title}",
            $"CI: {v.Pr.Ci}  Review: {Review(v.Pr)}",
            $"Base: {v.Pr.BaseRef}  Author: {v.Pr.Author}",
            $"\nGraph impact: {v.Nodes} nodes across {v.Communities.Count} communities",
            $"Communities touched: [{string.Join(", ", v.Communities)}]",
            $"Files changed ({files.Count}):",
        };
        lines.AddRange(files.Take(20).Select(f => $"  {f}"));
        if (files.Count > 20) lines.Add($"  … and {files.Count - 20} more");
        return string.Join("\n", lines);
    }

    public PrView? Find(string repo, int number) =>
        cache.OpenPrs.Repos.TryGetValue(repo, out var r) && r.Prs.FirstOrDefault(p => p.Number == number) is { } pr ? View(r, pr) : null;

    /// <summary>Runs one of the three tools on already validated arguments.</summary>
    public string Run(string tool, JsonObject args)
    {
        var repo = args["repo"]?.GetValue<string>();
        var baseBranch = args["base"]?.GetValue<string>();
        return tool switch
        {
            "list_prs" => ListPrs(repo, baseBranch),
            "triage_prs" => TriagePrs(repo, baseBranch),
            "get_pr_impact" => PrImpact(repo!, args["pr_number"]!.GetValue<int>()),
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };
    }

    // ---- The board's REST view ----

    public JsonObject List(IReadOnlyList<string> repos, bool? actionable)
    {
        var items = new JsonArray();
        foreach (var name in repos.Count > 0 ? repos : AllRepos.ToList())
        {
            if (!cache.OpenPrs.Repos.TryGetValue(name, out var r)) continue;
            var views = r.Prs.Select(p => View(r, p)).ToList();
            foreach (var v in views)
            {
                var isActionable = v.Pr.BaseRef == v.DefaultBase && v.Status is not ("WRONG-BASE" or "STALE");
                // true is the triage filter (triage_prs); false or absent lists every open PR (list_prs).
                if (actionable == true && !isActionable) continue;
                items.Add(Item(v, views));
            }
        }
        return new JsonObject { ["generatedAt"] = cache.OpenPrs.GeneratedAt, ["prs"] = items };
    }

    public JsonObject? Detail(string repo, int number)
    {
        if (!cache.OpenPrs.Repos.TryGetValue(repo, out var r) || r.Prs.FirstOrDefault(p => p.Number == number) is not { } pr) return null;
        var views = r.Prs.Select(p => View(r, p)).ToList();
        var item = Item(views.First(v => v.Pr.Number == number), views);
        item["files"] = new JsonArray(pr.Files.Take(100).Select(f => (JsonNode)f).ToArray());
        item["roomNames"] = new JsonArray(views.First(v => v.Pr.Number == number).Rooms.Select(id => (JsonNode)RoomName(repo, id)).ToArray());
        return item;
    }

    private string RoomName(string repo, string roomId) =>
        graphs.Graph(repo)?.Rooms.FirstOrDefault(r => r.Id == roomId)?.Name ?? roomId;

    /// <summary>
    /// The other open PRs into the same base branch that change at least one of the same files: the ones that can
    /// really collide on merge. Sharing a room (a graph community) is too broad on a big repo, where one large PR
    /// shares rooms with most others; the card still lists the rooms each PR touches. The shared files' rooms are what
    /// the map rings and joins.
    /// </summary>
    private JsonObject Item(PrView v, IReadOnlyList<PrView> sameRepo)
    {
        var conflicts = new JsonArray();
        var files = v.Pr.Files.ToHashSet(StringComparer.Ordinal);
        var fileRooms = graphs.Graph(v.Repo)?.FileRooms;
        foreach (var other in sameRepo)
        {
            if (other.Pr.Number == v.Pr.Number || other.Pr.BaseRef != v.Pr.BaseRef) continue;
            var shared = other.Pr.Files.Where(files.Contains).Distinct(StringComparer.Ordinal).ToList();
            if (shared.Count == 0) continue;
            var rooms = shared.SelectMany(f => fileRooms?.GetValueOrDefault(f) ?? []).Distinct(StringComparer.Ordinal).ToList();
            conflicts.Add(new JsonObject
            {
                ["number"] = other.Pr.Number,
                ["files"] = new JsonArray(shared.Take(20).Select(f => (JsonNode)f).ToArray()),
                ["rooms"] = new JsonArray(rooms.Select(s => (JsonNode)s).ToArray()),
                ["roomNames"] = new JsonArray(rooms.Select(s => (JsonNode)RoomName(v.Repo, s)).ToArray()),
            });
        }
        return new JsonObject
        {
            ["repo"] = v.Repo,
            ["number"] = v.Pr.Number,
            ["title"] = v.Pr.Title,
            ["author"] = v.Pr.Author,
            ["isDraft"] = v.Pr.IsDraft,
            ["ci"] = v.Pr.Ci,
            ["review"] = v.Pr.ReviewDecision,
            ["status"] = v.Status,
            ["base"] = v.Pr.BaseRef,
            ["defaultBase"] = v.DefaultBase,
            ["ageDays"] = v.AgeDays,
            ["updatedAt"] = v.Pr.UpdatedAt,
            ["labels"] = new JsonArray(v.Pr.Labels.Select(l => (JsonNode)l).ToArray()),
            ["mergeable"] = v.Pr.Mergeable,
            ["fileCount"] = v.Pr.Files.Count,
            ["rooms"] = new JsonArray(v.Rooms.Select(r => (JsonNode)r).ToArray()),
            ["communities"] = new JsonArray(v.Communities.Select(c => (JsonNode)c).ToArray()),
            ["nodes"] = v.Nodes,
            ["blastRadius"] = v.BlastRadius,
            ["url"] = $"https://github.com/{v.Repo}/pull/{v.Pr.Number}",
            ["conflictsWith"] = conflicts,
        };
    }
}
