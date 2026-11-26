using MudMaps.Shared;

namespace MudMaps.Server.Tests;

// The figures above and below each map follow the repo in view; with nothing in view they cover the whole map.
public class MapStatsTests
{
    private static readonly MapData Map = new(
        GeneratedAt: "2026-11-19T00:00:00Z",
        WindowDays: 30,
        Headline: new Headline(12, 9),
        ExtensionsPrs: 0,
        Hoods:
        [
            new Hood("mb:grid", "Data Grid", "MudBlazor", Size: 100, Prs: 6, TestSize: 40),
            new Hood("mb:tests", "Unit Tests", "MudBlazor", Size: 30, Prs: 0, TestSize: 30),
            new Hood("ext:core", "Core", "MudX", Size: 20, Prs: 3, TestSize: 5),
        ],
        Roads: [],
        Clusters: [],
        Others: [],
        Repos:
        [
            new RepoInfo("MudBlazor", "MudBlazor/MudBlazor", "https://github.com/MudBlazor/MudBlazor", 10, Prs: 9, Merged: 7, Nodes: 130, TestNodes: 70),
            new RepoInfo("MudX", "MudXtra/MudX", "https://github.com/MudXtra/MudX", 1, Prs: 3, Merged: 2, Nodes: 20, TestNodes: 5),
        ]);

    [Fact]
    public void Whole_map_adds_up_every_repo()
    {
        var s = MapStats.For(Map, null, includeTests: true);
        Assert.Equal(new MapStats(null, 2, 12, 9, 3, 150, 75), s);
    }

    [Fact]
    public void A_repo_counts_only_its_own_items()
    {
        var s = MapStats.For(Map, "MudBlazor", includeTests: true);
        Assert.Equal(new MapStats("MudBlazor", 1, 9, 7, 2, 130, 70), s);
    }

    [Fact]
    public void Hiding_tests_drops_test_items_and_test_only_areas_but_still_counts_the_tests()
    {
        var s = MapStats.For(Map, "MudBlazor", includeTests: false);
        Assert.Equal(1, s.Areas);
        Assert.Equal(60, s.CodeItems);
        Assert.Equal(70, s.TestItems);
    }

    [Fact]
    public void An_unknown_repo_falls_back_to_the_whole_map()
    {
        Assert.Null(MapStats.For(Map, "NotARepo", includeTests: true).Repo);
    }
}
