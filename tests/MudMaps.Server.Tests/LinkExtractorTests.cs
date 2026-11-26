using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MudMaps.Server.Maps;
using MudMaps.Server.Research;

namespace MudMaps.Server.Tests;

/// <summary>Golden tests on real graphify 0.9.108 answers (fakes/fixtures, captured from this host's graphs).</summary>
public class LinkExtractorTests
{
    private static readonly RepoGraph Theme = PrToolsTests.ThemeManager();

    private static LinkExtractor Extractor(params RepoGraph[] graphs) =>
        new(new PrToolsTests.Graphs(graphs.Length > 0 ? graphs : [Theme]), Options.Create(new MapsOptions()));

    private static string FixtureText(string name) =>
        ScrubberTests.Fixture(name)["result"]!["content"]![0]!["text"]!.GetValue<string>();

    [Fact]
    public void Query_graph_nodes_become_github_and_city_links_with_rooms()
    {
        var (links, rooms) = Extractor().Extract(FixtureText("query.json"), "MudBlazor/ThemeManager");
        var palette = links.Single(l => l!["label"]!.GetValue<string>() == "ThemePaletteColor")!;
        Assert.Equal("src/MudBlazor.ThemeManager/Models/ThemePaletteColor.cs", palette["file"]!.GetValue<string>());
        Assert.Equal(3, palette["line"]!.GetValue<int>());
        Assert.Equal("https://github.com/MudBlazor/ThemeManager/blob/HEAD/src/MudBlazor.ThemeManager/Models/ThemePaletteColor.cs#L3",
            palette["github"]!.GetValue<string>());
        var room = palette["roomId"]!.GetValue<string>();
        Assert.Contains(room, Theme.FileRooms["src/MudBlazor.ThemeManager/Models/ThemePaletteColor.cs"]);
        Assert.Equal($"../city/?repo=ThemeManager&room={Uri.EscapeDataString(room)}", palette["href"]!.GetValue<string>());
        // "NODE Palette [src= loc= ...]" names no file and gives no link.
        Assert.DoesNotContain(links, l => l!["label"]!.GetValue<string>() == "Palette");
        Assert.NotEmpty(rooms);
        Assert.All(rooms, r => Assert.Equal("MudBlazor/ThemeManager", r!["repo"]!.GetValue<string>()));
    }

    /// <summary>A one-word search on the merged graph comes back as graphify's "Ambiguous" candidate list; each candidate is a link in its own repo.</summary>
    [Fact]
    public void Ambiguous_candidates_on_the_merged_graph_become_links_in_their_repos()
    {
        var (links, _) = Extractor().Extract(FixtureText("merged-ambiguous.json"), null);
        Assert.Equal(11, links.Count);
        var input = links.Single(l => l!["file"]!.GetValue<string>() == "src/MudBlazor/Enums/InputType.cs")!;
        Assert.Equal("MudBlazor/MudBlazor", input["repo"]!.GetValue<string>());
        Assert.Equal("date", input["label"]!.GetValue<string>());
        Assert.Equal("https://github.com/MudBlazor/MudBlazor/blob/HEAD/src/MudBlazor/Enums/InputType.cs", input["github"]!.GetValue<string>());
        var cell = links.Single(l => l!["file"]!.GetValue<string>() == "Heron.MudCalendar/Components/CalendarCell.cs")!;
        Assert.Equal("danheron/Heron.MudCalendar", cell["repo"]!.GetValue<string>());
    }

    [Fact]
    public void Get_node_neighbors_and_community_lines_are_recognised()
    {
        var x = Extractor();
        var node = x.Extract(FixtureText("node.json"), "MudBlazor/ThemeManager").Links.Single()!;
        Assert.Equal("MudThemeManager", node["label"]!.GetValue<string>());
        Assert.Equal("src/MudBlazor.ThemeManager/Components/MudThemeManager.razor.cs", node["file"]!.GetValue<string>());
        Assert.Equal(7, node["line"]!.GetValue<int>());

        var neighbors = x.Extract(FixtureText("neighbors.json"), "MudBlazor/ThemeManager").Links;
        Assert.Equal(["ParameterState", "Palette", "EventCallback"], neighbors.Select(l => l!["label"]!.GetValue<string>()));
        Assert.Equal([12, 16, 48], neighbors.Select(l => l!["line"]!.GetValue<int>()));

        var members = x.Extract(FixtureText("community.json"), "MudBlazor/ThemeManager").Links;
        // Every listed member lives in ThemePaletteColor.cs: one link for the file, named after its first member.
        var member = members.Single()!;
        Assert.Equal("ThemePaletteColor", member["label"]!.GetValue<string>());
        Assert.Equal("src/MudBlazor.ThemeManager/Models/ThemePaletteColor.cs", member["file"]!.GetValue<string>());
        Assert.Null(member["line"]);
        // Truncation lines and headers are not members.
        Assert.DoesNotContain(members, l => l!["label"]!.GetValue<string>().Contains("TRUNCATED"));
    }

    [Fact]
    public void Merged_answers_resolve_the_repo_from_the_id_prefix_or_a_unique_file()
    {
        var mudblazor = Graph("MudBlazor/MudBlazor", "MudBlazor", ["src/MudBlazor/TScripts/mudPopover.ts", "src/MudBlazor/Components/DataGrid/MudDataGrid.razor.cs", "README.md"]);
        var mudx = Graph("MudXtra/MudX", "MudX", ["README.md"]);
        var x = Extractor(mudblazor, mudx);

        var node = x.Extract(FixtureText("merged-node.json"), null).Links.Single()!;
        Assert.Equal("MudBlazor/MudBlazor", node["repo"]!.GetValue<string>()); // from "ID: MudBlazor::..."
        Assert.Equal("https://github.com/MudBlazor/MudBlazor/blob/HEAD/src/MudBlazor/TScripts/mudPopover.ts#L916", node["github"]!.GetValue<string>());

        var query = x.Extract(FixtureText("merged-query.json"), null).Links;
        Assert.Contains(query, l => l!["file"]!.GetValue<string>() == "src/MudBlazor/Components/DataGrid/MudDataGrid.razor.cs"
            && l["repo"]!.GetValue<string>() == "MudBlazor/MudBlazor");
        // A file two repositories share cannot be placed, so it gets no link rather than a wrong one.
        Assert.Empty(x.Extract("NODE Readme [src=README.md loc=L1 community=x]", null).Links);
    }

    [Fact]
    public void File_names_are_url_encoded_per_segment()
    {
        Assert.Equal("https://github.com/a/b/blob/HEAD/docs/My%20File%23.md", LinkExtractor.GitHubUrl("a/b", "docs/My File#.md", null));
    }

    /// <summary>A tiny graph with one three-node community per file, so each file lands in a room.</summary>
    private static RepoGraph Graph(string fullName, string name, string[] files)
    {
        var nodes = new JsonArray();
        var c = 0;
        foreach (var f in files)
        {
            for (var i = 0; i < 3; i++) nodes.Add(new JsonObject { ["id"] = $"{f}#{i}", ["label"] = $"N{c}{i}", ["source_file"] = f, ["community"] = c });
            c++;
        }
        var json = new JsonObject { ["nodes"] = nodes, ["links"] = new JsonArray() }.ToJsonString();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return RepoGraph.Load(fullName, name, 10, stream, indexMudTypes: false);
    }
}
