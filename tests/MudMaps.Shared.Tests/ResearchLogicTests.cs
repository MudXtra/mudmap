using System.Text.Json;
using MudMaps.Shared;
using MudMaps.Shared.Research;

namespace MudMaps.Shared.Tests;

public class ResearchLogicTests
{
    private const string Mud = "MudBlazor/MudBlazor";
    private const string Ext = "fgilde/MudBlazor.Extensions";

    private static PrItem Pr(int number, string title = "Fix", string repo = Mud, string status = "PENDING", string author = "ann",
        string[]? labels = null, string[]? rooms = null, PrConflict[]? conflicts = null, double age = 1, int blast = 0,
        string? baseBranch = "dev", string? defaultBase = "dev") =>
        new(repo, number, title, author, false, "SUCCESS", "REVIEW_REQUIRED", status, baseBranch, defaultBase, age, null,
            labels ?? [], "MERGEABLE", 1, rooms ?? [], [], blast, blast == 0 ? "" : $"{blast} nodes / 1 community", $"https://github.com/{repo}/pull/{number}", conflicts ?? []);

    // ---- Search scope ----------------------------------------------------------------------------------------------

    [Fact]
    public void Scope_narrows_to_the_zoomed_repo_and_widens_on_request()
    {
        var scope = new SearchScope();
        Assert.Null(scope.Repo);
        Assert.True(scope.Follow(Mud));
        Assert.Equal(Mud, scope.Repo);
        scope.Widen();
        Assert.Null(scope.Repo);
        Assert.Equal(Mud, scope.Zoomed);
    }

    [Fact]
    public void Scope_stays_widened_in_the_same_repo_and_narrows_again_in_another()
    {
        var scope = new SearchScope();
        scope.Follow(Mud);
        scope.Widen();
        Assert.False(scope.Follow(Mud));
        Assert.Null(scope.Repo);
        Assert.True(scope.Follow(Ext));
        Assert.Equal(Ext, scope.Repo);
        Assert.True(scope.Follow(null));
        Assert.Null(scope.Repo);
    }

    // ---- PR autocomplete -------------------------------------------------------------------------------------------

    [Fact]
    public void Matcher_finds_by_number_title_author_repo_and_label()
    {
        var prs = new[]
        {
            Pr(12271, "DataGrid: server-side filter", author: "arcturus77", labels: ["enhancement"]),
            Pr(12266, "DateRangePicker: partial range", labels: ["bug"]),
            Pr(512, "Dialog focus", repo: Ext, author: "fgilde"),
        };
        Assert.Equal([12271], PrMatcher.Match(prs, "#12271").Select(p => p.Number));
        Assert.Equal([12271, 12266], PrMatcher.Match(prs, "122").Select(p => p.Number).Order().Reverse());
        Assert.Equal([12266], PrMatcher.Match(prs, "daterange").Select(p => p.Number));
        Assert.Equal([12271], PrMatcher.Match(prs, "arcturus").Select(p => p.Number));
        Assert.Equal([512], PrMatcher.Match(prs, "fgilde/MudBlazor.Extensions").Select(p => p.Number));
        Assert.Equal([12266], PrMatcher.Match(prs, "bug").Select(p => p.Number));
        Assert.Equal([512], PrMatcher.Match(prs, "Extensions dialog", r => r == Ext ? "MudBlazor.Extensions" : "MudBlazor").Select(p => p.Number));
        Assert.Empty(PrMatcher.Match(prs, "datagrid bug"));
    }

    [Fact]
    public void Matcher_ranks_exact_number_then_prefix_then_title_start()
    {
        var prs = new[] { Pr(1234, "Other"), Pr(123, "Thing"), Pr(9, "123 in the title") };
        Assert.Equal([123, 1234, 9], PrMatcher.Match(prs, "123").Select(p => p.Number));
    }

    [Fact]
    public void Matcher_with_no_query_lists_the_most_recent_first_up_to_max()
    {
        var prs = Enumerable.Range(1, 12).Select(i => Pr(i, age: i)).ToList();
        Assert.Equal([1, 2, 3], PrMatcher.Match(prs, "  ", max: 3).Select(p => p.Number));
    }

    // ---- Board -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("READY", BoardLane.Ready)]
    [InlineData("APPROVED", BoardLane.Ready)]
    [InlineData("PENDING", BoardLane.Waiting)]
    [InlineData("CI-FAIL", BoardLane.Other)]
    [InlineData("CHANGES-REQ", BoardLane.Other)]
    [InlineData("DRAFT", BoardLane.Other)]
    [InlineData("STALE", BoardLane.Other)]
    [InlineData("WRONG-BASE", BoardLane.Other)]
    public void Lane_follows_status_when_there_is_no_conflict(string status, BoardLane lane) =>
        Assert.Equal(lane, BoardGrouping.Lane(Pr(1, status: status)));

    [Fact]
    public void Lane_is_conflict_risk_whenever_another_pr_shares_a_room()
    {
        var pr = Pr(1, status: "READY", conflicts: [new PrConflict(2, ["mudblazor:1"], ["Filter"])]);
        Assert.Equal(BoardLane.Conflict, BoardGrouping.Lane(pr));
        Assert.Equal("Changes the same files as #2", BoardGrouping.ConflictReason(pr));
        var withFile = pr with { ConflictsWith = [new PrConflict(2, ["mudblazor:1"], ["Filter"], ["src/MudBlazor/Grid/Filter.cs"])] };
        Assert.Equal("Changes Filter.cs like #2", BoardGrouping.ConflictReason(withFile));
        // A merge conflict with the base branch is conflict risk even with no other PR involved.
        Assert.Equal(BoardLane.Conflict, BoardGrouping.Lane(Pr(3, status: "READY") with { Mergeable = "CONFLICTING" }));
    }

    [Fact]
    public void Group_filters_repos_and_actionable_and_orders_by_blast_radius()
    {
        var prs = new[]
        {
            Pr(1, status: "PENDING", blast: 3),
            Pr(2, status: "PENDING", blast: 9),
            Pr(3, status: "STALE"),
            Pr(4, status: "READY", baseBranch: "main", defaultBase: "dev"),
            Pr(5, repo: Ext, status: "READY"),
        };
        var all = BoardGrouping.Group(prs, [], actionable: false);
        Assert.Equal([BoardLane.Conflict, BoardLane.Waiting, BoardLane.Ready, BoardLane.Other], all.Select(l => l.Lane));
        Assert.Equal([2, 1], all[1].Prs.Select(p => p.Number));
        Assert.Equal([4, 5], all[2].Prs.Select(p => p.Number).Order());

        var mudActionable = BoardGrouping.Group(prs, [Mud], actionable: true);
        Assert.Equal([2, 1], mudActionable.SelectMany(l => l.Prs).Select(p => p.Number));
    }

    [Fact]
    public void Follow_map_uses_the_zoomed_repo_and_otherwise_the_picker()
    {
        var picked = new HashSet<string> { Mud, Ext };
        Assert.Equal(picked, BoardRepos.Effective(false, picked, Ext));
        Assert.Equal([Ext], BoardRepos.Effective(true, picked, Ext));
        Assert.Empty(BoardRepos.Effective(true, picked, null));
    }

    // ---- Conflicts -------------------------------------------------------------------------------------------------

    [Fact]
    public void Conflict_rooms_pair_the_prs_that_share_each_room()
    {
        var prs = new[]
        {
            Pr(12268, rooms: ["m:1", "m:2"], conflicts: [new PrConflict(12271, ["m:1"], null)]),
            Pr(12271, rooms: ["m:1", "m:3"], conflicts: [new PrConflict(12268, ["m:1"], null), new PrConflict(12259, ["m:3"], null)]),
            Pr(12259, rooms: ["m:3"], conflicts: [new PrConflict(12271, ["m:3"], null)]),
            Pr(7, repo: Ext, rooms: ["e:1"], conflicts: [new PrConflict(8, ["e:1"], null)]),
        };
        var rooms = ConflictMap.Rooms(prs, Mud);
        Assert.Equal(["m:1", "m:3"], rooms.Select(r => r.RoomId));
        Assert.Equal("#12268 + #12271", rooms[0].Label);
        Assert.Equal("#12259 + #12271", rooms[1].Label);
        Assert.Equal(3, ConflictMap.CountByRepo(prs)[Mud]);
        Assert.Equal(1, ConflictMap.CountByRepo(prs)[Ext]);
        // Lines are drawn for the focused PR only; with no focus there are none.
        Assert.Equal([(12271, (IReadOnlyList<string>)["m:1", "m:3"])],
            ConflictMap.Pins(prs, Mud, 12271).Select(p => (p.Number, p.Rooms)), new PinComparer());
        Assert.Empty(ConflictMap.Pins(prs, Mud, null));
        Assert.Empty(ConflictMap.Pins(prs, Mud, 7));
        // A room three PRs share is labelled with the count, not three numbers.
        Assert.Equal("3 PRs", new ConflictRoom("m:9", [1, 2, 3]).Label);
    }

    [Fact]
    public void Spread_fans_out_pins_on_the_same_spot_and_leaves_lone_pins()
    {
        var spread = ConflictMap.Spread([(10, 10), (10, 10), (11, 10), (500, 500)], 40);
        Assert.Equal((500d, 500d), spread[3]);
        for (var i = 0; i < 3; i++)
        {
            for (var j = i + 1; j < 3; j++)
            {
                var d = Math.Sqrt(Math.Pow(spread[i].X - spread[j].X, 2) + Math.Pow(spread[i].Y - spread[j].Y, 2));
                Assert.True(d >= 39, $"pins {i} and {j} are {d} apart");
            }
        }
    }

    // ---- Lens, figures, ids, MCP -----------------------------------------------------------------------------------

    [Fact]
    public void Lens_lights_rooms_their_areas_and_counts_rooms_per_repo()
    {
        var data = new MapData("now", 30, new Headline(0, 0), 0,
            [new Hood("mud/grid", "Data Grid", "MudBlazor", 10, 0), new Hood("mud/menu", "Menu", "MudBlazor", 5, 0), new Hood("ext/x", "X", "Ext", 5, 0)],
            [], [new Cluster("mud:1", "mud/grid", 5, "", []), new Cluster("mud:2", "mud/grid", 5, "", []), new Cluster("mud:3", "mud/menu", 5, "", []),
                new Cluster("ext:1", "ext/x", 5, "", [])], []);
        var lens = new MapLens("q", [new LensRoom(Mud, "mud:1", 2), new LensRoom(Mud, "mud:2", 1), new LensRoom(Mud, "gone:9", 1)], data);
        Assert.True(lens.Lit(data.Clusters[0]));
        Assert.False(lens.Lit(data.Clusters[2]));
        Assert.True(lens.Lit(data.Hoods[0]));
        Assert.False(lens.Lit(data.Hoods[1]));
        Assert.Equal(2, lens.Count("MudBlazor"));
        Assert.Equal(0, lens.Count("Ext"));
        Assert.Equal(2, lens.RoomCount);
        Assert.Null(lens.OnlyRoom);
    }

    [Fact]
    public void Graph_figures_read_graph_stats_text()
    {
        var f = GraphFigures.Parse("Nodes: 143210\nEdges: 401002\nCommunities: 812\nEXTRACTED: 90%\n");
        Assert.Equal(new GraphFigures(143210, 401002, 812), f);
        Assert.Null(GraphFigures.Parse("Graph not loaded"));
    }

    [Theory]
    [InlineData("mudblazor:84", 84)]
    [InlineData("codebeam-extensions:0", 0)]
    [InlineData("mudblazor", null)]
    [InlineData("mudblazor:x", null)]
    public void Room_ids_carry_the_graphify_community(string id, int? community) =>
        Assert.Equal(community, RoomIds.Community(id));

    [Fact]
    public void Mcp_call_is_a_tools_call_request()
    {
        using var doc = JsonDocument.Parse(McpCall.Build("query_graph", new Dictionary<string, object?> { ["question"] = "date", ["depth"] = 3 }));
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("tools/call", root.GetProperty("method").GetString());
        Assert.Equal("query_graph", root.GetProperty("params").GetProperty("name").GetString());
        Assert.Equal(3, root.GetProperty("params").GetProperty("arguments").GetProperty("depth").GetInt32());
    }

    /// <summary>The board reads the service's PR JSON as sent: blast radius is graphify's text, not a number.</summary>
    [Fact]
    public void PrListReadsTheServiceShape()
    {
        const string json = """
            {"generatedAt":"2026-11-23T19:00:00Z","prs":[{"repo":"MudBlazor/MudBlazor","number":12268,"title":"Filter","author":"ann",
            "isDraft":false,"ci":"SUCCESS","review":"","status":"READY","base":"dev","defaultBase":"dev","ageDays":3,
            "updatedAt":"2026-11-20T10:00:00Z","labels":["bug"],"mergeable":"MERGEABLE","fileCount":2,"rooms":["mudblazor:4"],
            "communities":[4,7],"nodes":28,"blastRadius":"28 nodes / 2 communities","url":"https://github.com/MudBlazor/MudBlazor/pull/12268",
            "conflictsWith":[{"number":12271,"rooms":["mudblazor:4"],"roomNames":["Filter"]}]}]}
            """;
        var list = JsonSerializer.Deserialize<PrList>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var pr = Assert.Single(list.Prs!);
        Assert.Equal("28 nodes / 2 communities", pr.BlastRadius);
        Assert.Equal(28, pr.Nodes);
        Assert.Equal(12271, Assert.Single(pr.Conflicts).Number);
    }

    private sealed class PinComparer : IEqualityComparer<(int, IReadOnlyList<string>)>
    {
        public bool Equals((int, IReadOnlyList<string>) a, (int, IReadOnlyList<string>) b) => a.Item1 == b.Item1 && a.Item2.SequenceEqual(b.Item2);
        public int GetHashCode((int, IReadOnlyList<string>) x) => x.Item1;
    }
}
