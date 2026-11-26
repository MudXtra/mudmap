using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudMaps.Server.GitHub;
using MudMaps.Server.Maps;
using MudMaps.Server.Research;

namespace MudMaps.Server.Tests;

public class PrToolsTests
{
    internal const string Repo = "MudBlazor/ThemeManager";
    internal static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    internal const string Palette = "src/MudBlazor.ThemeManager/Models/ThemePaletteColor.cs";
    internal const string Manager = "MudBlazor.ThemeManager/Components/MudThemeManager.razor.cs";

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal sealed class Cache(OpenPrSnapshot snapshot) : IPrCacheSnapshot
    {
        public OpenPrSnapshot OpenPrs => snapshot;
    }

    internal sealed class Graphs(params RepoGraph[] graphs) : IRepoGraphs
    {
        public RepoGraph? Graph(string fullName) => graphs.FirstOrDefault(g => g.FullName == fullName);
    }

    internal static RepoGraph ThemeManager()
    {
        using var s = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fakes", "fixtures", "thememanager-graph.json"));
        return RepoGraph.Load(Repo, "ThemeManager", 3, s, indexMudTypes: false);
    }

    internal static OpenPr Pr(int number, string baseRef = "dev", string ci = "SUCCESS", string review = "", bool draft = false,
        int ageDays = 1, string[]? files = null, string title = "A change") =>
        new(number, title, "octocat", baseRef, draft, review, ci, Now.AddDays(-ageDays).AddHours(-1), "MERGEABLE", ["bug"], files ?? []);

    internal static OpenPrSnapshot Snapshot(params OpenPr[] prs) =>
        new(Now, new Dictionary<string, RepoOpenPrs> { [Repo] = new(Repo, "dev", prs, Now) });

    private static PrTools Tools(OpenPrSnapshot snapshot) =>
        new(new Cache(snapshot), new Graphs(ThemeManager()),
            Options.Create(new MapsOptions { Repos = [new(Repo, "ThemeManager", 3), new("MudXtra/MudX", "MudX", 6)] }), new FixedClock(Now));

    [Theory]
    [InlineData("main", "FAILURE", "CHANGES_REQUESTED", true, 30, "WRONG-BASE")]
    [InlineData("dev", "FAILURE", "CHANGES_REQUESTED", true, 30, "CI-FAIL")]
    [InlineData("dev", "SUCCESS", "CHANGES_REQUESTED", true, 30, "CHANGES-REQ")]
    [InlineData("dev", "SUCCESS", "APPROVED", true, 30, "DRAFT")]
    [InlineData("dev", "SUCCESS", "APPROVED", false, 14, "STALE")]
    [InlineData("dev", "PENDING", "APPROVED", false, 13, "APPROVED")]
    [InlineData("dev", "PENDING", "", false, 13, "PENDING")]
    [InlineData("dev", "NONE", "", false, 0, "READY")]
    public void Classification_follows_graphifys_order(string baseRef, string ci, string review, bool draft, int age, string status) =>
        Assert.Equal(status, PrTools.Classify(Pr(1, baseRef, ci, review, draft), "dev", age));

    [Theory]
    [InlineData("FAILURE", "FAILURE")]
    [InlineData("ERROR", "FAILURE")]
    [InlineData("PENDING", "PENDING")]
    [InlineData("EXPECTED", "PENDING")]
    [InlineData("SUCCESS", "SUCCESS")]
    [InlineData(null, "NONE")]
    public void Ci_rollup_maps_like_parse_ci(string? state, string ci) => Assert.Equal(ci, OpenPrParser.Ci(state));

    [Theory]
    [InlineData("src/A.cs", "src/A.cs", true)]
    [InlineData("repo/src/A.cs", "src/A.cs", true)]
    [InlineData("src/A.cs", "repo/src/A.cs", true)]
    [InlineData("src/BA.cs", "A.cs", false)]
    [InlineData("src/A.cs", "src/A.csx", false)]
    public void Path_match_is_boundary_safe(string graphSrc, string prFile, bool match) => Assert.Equal(match, PrTools.PathMatch(graphSrc, prFile));

    // Expected values computed by graphify 0.9.108's own prs.compute_pr_impact on the same graph.json.
    [Theory]
    [InlineData(new[] { Palette }, new[] { 0, 1 }, 28)]
    [InlineData(new[] { Manager, "README.md", "nope/x.cs" }, new[] { 1, 2 }, 24)]
    [InlineData(new[] { "ThemePaletteColor.cs" }, new[] { 0, 1 }, 28)]
    public void Impact_matches_graphify_compute_pr_impact(string[] files, int[] communities, int nodes)
    {
        var (c, n, rooms) = PrTools.Impact(files, ThemeManager());
        Assert.Equal(communities, c);
        Assert.Equal(nodes, n);
        Assert.NotEmpty(rooms);
    }

    [Fact]
    public void List_prs_text_mirrors_format_prs_text()
    {
        var tools = Tools(Snapshot(Pr(7, files: [Palette]), Pr(8, ci: "FAILURE"), Pr(9, baseRef: "main")));
        Assert.Equal(
            "Open PRs targeting dev: 2  (1 on wrong base, not shown)\n\n\n" +
            "#8 [CI-FAIL] CI=FAILURE review=none age=1d author=octocat\n  A change\n\n" +
            "#7 [READY] CI=SUCCESS review=none age=1d author=octocat  blast_radius=28 nodes / 2 communities\n  A change",
            tools.ListPrs(Repo, null));
    }

    [Fact]
    public void Triage_text_mirrors_graphify_without_worktrees()
    {
        var tools = Tools(Snapshot(Pr(7, files: [Palette], review: "APPROVED"), Pr(8, ageDays: 20), Pr(9, baseRef: "main")));
        Assert.Equal(
            "Actionable PRs targeting dev: 1\n" +
            "Rank these by review priority. Higher blast_radius = more graph communities affected = higher merge risk.\n\n\n" +
            "PR #7 [APPROVED] CI=SUCCESS review=APPROVED age=1d author=octocat  blast_radius=28 nodes / 2 communities\n  title: A change",
            tools.TriagePrs(Repo, null));
        Assert.Equal("No actionable PRs targeting main.", Tools(Snapshot(Pr(8, ageDays: 20, baseRef: "main"))).TriagePrs(Repo, "main"));
    }

    [Fact]
    public void Pr_impact_text_mirrors_graphify_and_cuts_files_at_20()
    {
        var files = new[] { Palette }.Concat(Enumerable.Range(1, 22).Select(i => $"docs/f{i}.md")).ToArray();
        var text = Tools(Snapshot(Pr(7, files: files, title: "Fix palette"))).PrImpact(Repo, 7);
        var lines = text.Split('\n');
        Assert.Equal("PR #7: Fix palette", lines[0]);
        Assert.Equal("CI: SUCCESS  Review: none", lines[1]);
        Assert.Equal("Base: dev  Author: octocat", lines[2]);
        Assert.Equal("", lines[3]);
        Assert.Equal("Graph impact: 28 nodes across 2 communities", lines[4]);
        Assert.Equal("Communities touched: [0, 1]", lines[5]);
        Assert.Equal("Files changed (23):", lines[6]);
        Assert.Equal($"  {Palette}", lines[7]);
        Assert.Equal("  … and 3 more", lines[^1]);
        Assert.Equal(28, lines.Length);
    }

    [Fact]
    public void Unknown_pr_and_uncached_repo_are_named_never_fetched()
    {
        var tools = Tools(Snapshot(Pr(7)));
        Assert.Equal("PR #99 is not in the Mud Maps cache (open PRs, refreshed every 10 min).", tools.PrImpact(Repo, 99));
        var all = tools.ListPrs(null, null);
        Assert.StartsWith("== MudBlazor/ThemeManager ==\nOpen PRs targeting dev: 1", all);
        Assert.Contains("\n\n== MudXtra/MudX ==\nOpen PRs for MudXtra/MudX are not in the Mud Maps cache", all);
    }

    /// <summary>Two PRs in the same room but on different files are not a conflict: on a big repo almost every PR shares a room.</summary>
    [Fact]
    public void Sharing_a_room_without_a_file_is_not_a_conflict()
    {
        var (room, pair) = ThemeManager().FileRooms.SelectMany(kv => kv.Value.Select(r => (Room: r, File: kv.Key)))
            .GroupBy(x => x.Room).Where(g => g.Count() >= 2).Select(g => (g.Key, g.Take(2).Select(x => x.File).ToArray())).First();
        var tools = Tools(Snapshot(Pr(7, files: [pair[0]]), Pr(8, files: [pair[1]])));
        var items = tools.List([], null)["prs"]!.AsArray();
        Assert.All(items, i => Assert.Contains(room, i!["rooms"]!.AsArray().Select(r => r!.GetValue<string>())));
        Assert.All(items, i => Assert.Empty(i!["conflictsWith"]!.AsArray()));
    }

    [Fact]
    public void Board_lists_actionable_prs_with_rooms_and_conflicts()
    {
        var tools = Tools(Snapshot(Pr(7, files: [Palette]), Pr(8, files: [Palette, "README.md"]), Pr(9, baseRef: "main", files: [Palette]), Pr(10, ageDays: 30)));
        var all = tools.List([], null)["prs"]!.AsArray();
        Assert.Equal(4, all.Count);
        var actionable = tools.List([Repo], true)["prs"]!.AsArray().Select(p => p!["number"]!.GetValue<int>());
        Assert.Equal([7, 8], actionable);
        // false is not the inverse filter: it lists every open PR, as the board asks for.
        Assert.Equal([7, 8, 9, 10], tools.List([Repo], false)["prs"]!.AsArray().Select(p => p!["number"]!.GetValue<int>()).Order());
        var seven = all.Single(p => p!["number"]!.GetValue<int>() == 7)!;
        Assert.Equal("https://github.com/MudBlazor/ThemeManager/pull/7", seven["url"]!.GetValue<string>());
        Assert.Equal("28 nodes / 2 communities", seven["blastRadius"]!.GetValue<string>());
        // #9 changes the same file but targets another base branch, so it cannot collide with #7 on merge.
        var conflict = Assert.Single(seven["conflictsWith"]!.AsArray())!;
        Assert.Equal(8, conflict["number"]!.GetValue<int>());
        Assert.Equal([Palette], conflict["files"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.NotEmpty(seven["rooms"]!.AsArray());
        var detail = tools.Detail(Repo, 8)!;
        Assert.Equal([Palette, "README.md"], detail["files"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal(detail["rooms"]!.AsArray().Count, detail["roomNames"]!.AsArray().Count);
        Assert.Null(tools.Detail(Repo, 99));
    }

    [Fact]
    public void Github_answer_is_cleaned_and_capped()
    {
        var label = new JsonObject { ["name"] = "l" + new string('x', 60) };
        var json = new JsonObject
        {
            ["defaultBranchRef"] = new JsonObject { ["name"] = "dev" },
            ["pullRequests"] = new JsonObject
            {
                ["nodes"] = new JsonArray(
                    Node(1, "Fix\u0007 the\nthing " + new string('t', 250), new JsonObject { ["__typename"] = "User", ["login"] = "evil user" }, label, "src/\u001bA.cs"),
                    Node(2, "Bump", new JsonObject { ["__typename"] = "Bot", ["login"] = "renovate[bot]" }, label, new string('p', 400)),
                    Node(3, "Ghost", null, label, "a.cs")),
            },
        };
        var parsed = OpenPrParser.Parse(Repo, JsonDocument.Parse(json.ToJsonString()).RootElement, Now);
        Assert.Equal("dev", parsed.DefaultBranch);
        var one = parsed.Prs[0];
        Assert.Equal(200, one.Title.Length);
        Assert.StartsWith("Fix thething ", one.Title);
        Assert.DoesNotContain(one.Title, char.IsControl);
        Assert.Equal("?", one.Author);
        Assert.Equal(10, one.Labels.Count);
        Assert.All(one.Labels, l => Assert.Equal(50, l.Length));
        Assert.Equal("src/A.cs", one.Files[0]);
        Assert.Equal("FAILURE", one.Ci);
        Assert.Equal("renovate[bot]", parsed.Prs[1].Author);
        Assert.Equal(300, parsed.Prs[1].Files[0].Length);
        Assert.Equal("?", parsed.Prs[2].Author);
    }

    private static JsonObject Node(int number, string title, JsonObject? author, JsonObject label, string file) => new()
    {
        ["number"] = number,
        ["title"] = title,
        ["headRefName"] = "feature",
        ["baseRefName"] = "dev",
        ["isDraft"] = false,
        ["reviewDecision"] = null,
        ["updatedAt"] = "2026-11-20T10:00:00Z",
        ["mergeable"] = "CONFLICTING",
        ["author"] = author,
        ["labels"] = new JsonObject { ["nodes"] = new JsonArray(Enumerable.Range(0, 12).Select(_ => (JsonNode)label.DeepClone()).ToArray()) },
        ["files"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["path"] = file }) },
        ["commits"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["commit"] = new JsonObject { ["statusCheckRollup"] = new JsonObject { ["state"] = "ERROR" } } }) },
    };

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public static int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("the PR tools must never reach GitHub");
        }
    }

    [Fact]
    public void Pr_tools_cannot_reach_a_github_client()
    {
        var dependencies = typeof(PrTools).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToList();
        Assert.DoesNotContain(typeof(GitHubActivityClient), dependencies);
        Assert.DoesNotContain(typeof(HttpClient), dependencies);
        Assert.DoesNotContain(typeof(IHttpClientFactory), dependencies);
    }

    [Fact]
    public async Task All_pr_tools_and_board_endpoints_answer_from_cache_with_a_throwing_github_client()
    {
        ThrowingHandler.Calls = 0;
        await using var h = new ResearchHarness(services: s =>
            s.AddHttpClient<GitHubActivityClient>().ConfigurePrimaryHttpMessageHandler(() => new ThrowingHandler()));
        var state = h.App.Services.GetRequiredService<MapState>();
        state.Graphs = new Dictionary<string, RepoGraph> { ["MudBlazor/ThemeManager"] = ThemeManager() };
        state.OpenPrs = new OpenPrSnapshot(Now, new Dictionary<string, RepoOpenPrs>
        {
            ["MudBlazor/ThemeManager"] = new("MudBlazor/ThemeManager", "dev", [Pr(7, files: [Palette])], Now),
        });
        var client = h.Client();
        foreach (var call in new[]
        {
            GatewayTests.Call("list_prs", "{}"),
            GatewayTests.Call("get_pr_impact", """{"pr_number":7,"repo":"MudBlazor/ThemeManager"}"""),
            GatewayTests.Call("get_pr_impact", """{"pr_number":8,"repo":"MudBlazor/ThemeManager"}"""),
            GatewayTests.Call("triage_prs", """{"repo":"MudBlazor/ThemeManager"}"""),
        })
        {
            var (status, body, _) = await GatewayTests.Post(client, "/mcp", call);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.NotNull(body!["result"]);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/research/prs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/research/pr?repo=MudBlazor/ThemeManager&number=7")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/research/pr?repo=MudBlazor/ThemeManager&number=8")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/research/prs?repo=someone/else")).StatusCode);
        Assert.Equal(0, ThrowingHandler.Calls);
        // No PR call reached graphify either.
        Assert.DoesNotContain(h.ToolCalls(), c => c["params"]!["name"]!.GetValue<string>().Contains("pr"));
    }

    [Fact]
    public async Task Triage_is_limited_to_two_a_minute_per_client()
    {
        await using var h = new ResearchHarness();
        var client = h.Client("203.0.113.44");
        var triage = GatewayTests.Call("triage_prs", "{}");
        Assert.Equal(HttpStatusCode.OK, (await GatewayTests.Post(client, "/mcp", triage)).Status);
        Assert.Equal(HttpStatusCode.OK, (await GatewayTests.Post(client, "/api/research", """{"tool":"triage_prs"}""")).Status);
        var (status, _, response) = await GatewayTests.Post(client, "/mcp", triage);
        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await GatewayTests.Post(client, "/mcp", GatewayTests.Call("list_prs", "{}"))).Status);
    }

    [Fact]
    public async Task Board_reads_are_limited_to_60_a_minute()
    {
        await using var h = new ResearchHarness(new() { ["Maps:Research:PrReadsPerMinute"] = "2" });
        var client = h.Client("203.0.113.45");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/research/prs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/research/pr?repo=MudXtra/MudX&number=1")).StatusCode);
        DeliveryTests.AssertLimitedResponse(await client.GetAsync("/api/research/prs"));
    }
}
