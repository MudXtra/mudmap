using System.Text;
using MudMaps.Server.Maps;

namespace MudMaps.Server.Tests;

public class MapBuildTests
{
    // Two areas (DataGrid, Chart), three communities, one external MudBlazor type reference.
    private const string Graph = """
        {
          "built_at_commit": "abc123",
          "nodes": [
            {"id": "g1", "label": "MudDataGrid", "source_file": "src/MudBlazor/Components/DataGrid/MudDataGrid.razor.cs", "community": 1},
            {"id": "g2", "label": "Column", "source_file": "src/MudBlazor/Components/DataGrid/Column.cs", "community": 1},
            {"id": "g3", "label": "DataGridTests", "source_file": "src/MudBlazor.UnitTests/Components/DataGridTests.cs", "community": 1},
            {"id": "c1", "label": "MudChart", "source_file": "src/MudBlazor/Components/Chart/MudChart.razor.cs", "community": 2},
            {"id": "c2", "label": "ChartOptions", "source_file": "src/MudBlazor/Components/Chart/ChartOptions.cs", "community": 2},
            {"id": "c3", "label": "BarChart", "source_file": "src/MudBlazor/Components/Chart/BarChart.razor", "community": 2},
            {"id": "x1", "label": "tiny", "source_file": "src/MudBlazor/Components/Chart/Tiny.cs", "community": 3},
            {"id": "j1", "label": "junk", "source_file": "src/MudBlazor/wwwroot/lib/x.min.js", "community": 4},
            {"id": "e1", "label": "MudDialog"}
          ],
          "links": [
            {"source": "g1", "target": "g2"}, {"source": "g1", "target": "g3"}, {"source": "g1", "target": "c1"},
            {"source": "c1", "target": "c2"}, {"source": "c1", "target": "c3"}, {"source": "c1", "target": "e1"}
          ]
        }
        """;

    private static RepoGraph Load(string name = "MudBlazor", int budget = 5, bool index = true) =>
        RepoGraph.Load("MudBlazor/MudBlazor", name, budget, new MemoryStream(Encoding.UTF8.GetBytes(Graph)), index);

    [Fact]
    public void Areas_rooms_and_roads_come_from_the_graph()
    {
        var g = Load();
        Assert.Equal(["mudblazor/datagrid", "mudblazor/chart"], g.Areas.Select(a => a.Key).Order().Reverse());
        Assert.Equal(7, g.Nodes); // junk and file-less nodes are not code items
        Assert.Equal(2, g.Rooms.Count); // community 3 has fewer than three members
        Assert.Equal("MudDataGrid", g.Rooms.Single(r => r.Hood == "mudblazor/datagrid").Name);
        Assert.Equal(1, g.Roads[("mudblazor/chart", "mudblazor/datagrid")]);
        Assert.Equal("abc123", g.BuiltAtCommit);
    }

    [Fact]
    public void Files_at_the_repo_root_become_rooms_without_error()
    {
        const string flat = """
            {"nodes": [
              {"id": "a", "label": "Program", "source_file": "Program.cs", "community": 1},
              {"id": "b", "label": "Startup", "source_file": "Program.cs", "community": 1},
              {"id": "c", "label": "Main", "source_file": "src/Program.cs", "community": 1}
            ], "links": []}
            """;
        var g = RepoGraph.Load("o/r", "Flat", 5, new MemoryStream(Encoding.UTF8.GetBytes(flat)), false);
        var room = Assert.Single(g.Rooms);
        Assert.Equal("Program", room.Name);
        Assert.Equal("", room.Dir);
    }

    [Fact]
    public void Small_budget_folds_the_rest_into_everything_else()
    {
        var g = Load(budget: 1);
        Assert.True(g.HasOther);
        Assert.Equal("mudblazor/other", g.HoodKeyFor("DataGrid") == "mudblazor/datagrid" ? g.HoodKeyFor("Chart") : g.HoodKeyFor("DataGrid"));
    }

    [Fact]
    public void Pull_requests_land_on_areas_and_rooms_by_file()
    {
        var g = Load();
        var now = DateTimeOffset.Parse("2026-11-16T00:00:00Z");
        var prs = new List<PullRequestInfo>
        {
            new(1, now.AddDays(-2), now.AddDays(-1), ["src/MudBlazor/Components/DataGrid/Column.cs", "src/MudBlazor.UnitTests/Components/DataGridTests.cs"]),
            new(2, now.AddDays(-3), null, ["src/MudBlazor/Components/Chart/BarChart.razor", "README.md"]),
            new(3, now.AddDays(-40), now.AddDays(-39), ["src/MudBlazor/Components/Chart/MudChart.razor.cs"]), // outside the window
        };
        var activity = new Dictionary<string, RepoActivity> { ["MudBlazor/MudBlazor"] = new("MudBlazor/MudBlazor", "https://github.com/MudBlazor/MudBlazor", 10000, prs) };

        var map = MapComposer.Compose([g], activity, 30, now, prsLive: true);

        Assert.Equal(1, map.Hoods.Single(h => h.Key == "mudblazor/datagrid").Prs);
        Assert.Equal(1, map.Hoods.Single(h => h.Key == "mudblazor/chart").Prs);
        Assert.Equal(1, map.Clusters.Single(c => c.Hood == "mudblazor/datagrid").Prs);
        var repo = Assert.Single(map.Repos);
        Assert.Equal((2, 1, 10000), (repo.Prs, repo.Merged, repo.Stars));
        Assert.Equal(new HeadlineDto(2, 1), map.Headline);
        Assert.True(map.PrsLive);
    }

    [Fact]
    public void Repo_highlights_name_the_top_contributor_and_carry_the_facts()
    {
        var at = DateTimeOffset.Parse("2026-11-10T00:00:00Z");
        var facts = new RepoFacts("A library", "v9.14.0", "v9.14.0", at, "https://x", at.AddDays(2), at.AddDays(3), 87, 537, 1713, "MIT");
        List<PullRequestInfo> prs =
        [
            new(1, at, at, [], "alice"), new(2, at, null, [], "alice"), new(3, at, at, [], "bob"),
            new(4, at, at, [], "bob"), new(5, at, null, [], null),
        ];

        var h = MapComposer.Highlights(facts, prs)!;

        Assert.Equal(("bob", 2, 2), (h.TopAuthor, h.TopAuthorPrs, h.TopAuthorMerged)); // tie on PRs, bob merged more
        Assert.Equal(2, h.Contributors);
        Assert.Equal(("v9.14.0", 87, 537, "MIT"), (h.Version, h.OpenPrs, h.OpenIssues, h.License));
        Assert.Null(MapComposer.Highlights(null, prs));
    }

    [Fact]
    public void Extension_repos_get_roads_into_mudblazor_types()
    {
        const string ext = """
            {"nodes": [
              {"id": "a", "label": "MudExDialog", "source_file": "MudBlazor.Extensions/Components/MudExDialog.razor.cs", "community": 1},
              {"id": "b", "label": "MudChart"}
            ], "links": [{"source": "a", "target": "b"}]}
            """;
        var mud = Load();
        var extension = RepoGraph.Load("fgilde/MudBlazor.Extensions", "MudBlazor.Extensions", 5, new MemoryStream(Encoding.UTF8.GetBytes(ext)), false);

        var map = MapComposer.Compose([mud, extension], new Dictionary<string, RepoActivity>(), 30, DateTimeOffset.UtcNow, prsLive: false);

        Assert.Contains(map.Roads, r => r is { A: "mudblazor-extensions/dialog", B: "mudblazor/chart" } or { A: "mudblazor/chart", B: "mudblazor-extensions/dialog" });
        Assert.False(map.PrsLive);
    }

    [Fact]
    public void Token_expiry_header_parses_and_warns_two_weeks_out()
    {
        var at = MudMaps.Server.GitHub.GitHubActivityClient.ParseTokenExpiry("2027-11-01 05:00:00 UTC");
        Assert.Equal(DateTimeOffset.Parse("2027-11-01T05:00:00Z"), at);
        Assert.Null(MudMaps.Server.GitHub.GitHubActivityClient.ParseTokenExpiry(null));
        var state = new MapState { TokenExpiresAt = at };
        Assert.False(state.TokenExpiringSoon(DateTimeOffset.Parse("2027-10-01T00:00:00Z")));
        Assert.True(state.TokenExpiringSoon(DateTimeOffset.Parse("2027-10-25T00:00:00Z")));
    }
}
