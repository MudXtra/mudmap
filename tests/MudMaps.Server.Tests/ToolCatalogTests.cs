using System.Text.Json.Nodes;
using MudMaps.Server.Research;

namespace MudMaps.Server.Tests;

public class ToolCatalogTests
{
    private static readonly string[] Repos = ["MudBlazor/MudBlazor", "MudXtra/MudX"];
    private readonly ToolCatalog _catalog = new(Repos);

    private string? Check(string tool, string args) => _catalog.Validate(tool, JsonNode.Parse(args)!.AsObject());

    [Fact]
    public void Tools_list_has_graphifys_ten_tools_with_repo_enums_on_every_tool()
    {
        var tools = _catalog.ToolsListResult["tools"]!.AsArray();
        Assert.Equal(
            ["query_graph", "get_node", "get_neighbors", "get_community", "god_nodes", "graph_stats", "shortest_path", "list_prs", "get_pr_impact", "triage_prs"],
            tools.Select(t => t!["name"]!.GetValue<string>()));
        foreach (var tool in tools)
        {
            var name = tool!["name"]!.GetValue<string>();
            var props = tool["inputSchema"]!["properties"]!.AsObject();
            var repoProp = ToolCatalog.PrTools.Contains(name) ? "repo" : "project_path";
            Assert.Equal(Repos, props[repoProp]!["enum"]!.AsArray().Select(e => e!.GetValue<string>()));
            if (ToolCatalog.PrTools.Contains(name)) Assert.False(props.ContainsKey("project_path"));
            // Every integer, number and string carries a bound, except enums and booleans.
            foreach (var (arg, p) in props)
            {
                var type = p!["type"]!.GetValue<string>();
                if (type is "integer" or "number") Assert.True(p["minimum"] is not null && p["maximum"] is not null, $"{name}.{arg}");
                if (type == "string" && p["enum"] is null) Assert.True(p["maxLength"] is not null, $"{name}.{arg}");
                if (type == "array") Assert.NotNull(p["maxItems"]);
            }
        }
        // graphify's own wording survives (descriptions copied, not paraphrased).
        Assert.Equal("Get full details for a specific node by label or ID.", tools[1]!["description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("query_graph", """{"question":"x","token_budget":100}""", null)]
    [InlineData("query_graph", """{"question":"x","token_budget":8000}""", null)]
    [InlineData("query_graph", """{"question":"x","token_budget":99}""", "token_budget must be between 100 and 8000")]
    [InlineData("query_graph", """{"question":"x","token_budget":8001}""", "token_budget must be between 100 and 8000")]
    [InlineData("query_graph", """{"question":"x","depth":1}""", null)]
    [InlineData("query_graph", """{"question":"x","depth":6}""", null)]
    [InlineData("query_graph", """{"question":"x","depth":0}""", "depth must be between 1 and 6")]
    [InlineData("query_graph", """{"question":"x","depth":7}""", "depth must be between 1 and 6")]
    [InlineData("query_graph", """{"question":"x","depth":2.5}""", "depth must be an integer")]
    [InlineData("query_graph", """{"question":"x","depth":"2"}""", "depth must be an integer")]
    [InlineData("query_graph", """{"question":"x","mode":"dfs"}""", null)]
    [InlineData("query_graph", """{"question":"x","mode":"astar"}""", "mode must be one of: bfs, dfs")]
    [InlineData("query_graph", """{"question":""}""", "question must be 1-300 characters")]
    [InlineData("query_graph", """{}""", "question is required")]
    [InlineData("query_graph", """{"question":"a\u0007b"}""", "question must not contain control characters")]
    [InlineData("query_graph", """{"question":"x","context_filter":["call","a","b","c","d","e","f","g"]}""", null)]
    [InlineData("query_graph", """{"question":"x","context_filter":["call","a","b","c","d","e","f","g","h"]}""", "context_filter may have at most 8 items")]
    [InlineData("query_graph", """{"question":"x","context_filter":["Call"]}""", "context_filter item must match ^[a-z_]{1,32}$")]
    [InlineData("query_graph", """{"question":"x","demote_docs":"yes"}""", "demote_docs must be a boolean")]
    [InlineData("query_graph", """{"question":"x","project_path":"MudXtra/MudX"}""", null)]
    [InlineData("query_graph", """{"question":"x","project_path":"/etc"}""", "project_path must be one of: MudBlazor/MudBlazor, MudXtra/MudX")]
    [InlineData("query_graph", """{"question":"x","bogus":1}""", "unknown argument 'bogus' for query_graph")]
    [InlineData("get_node", """{}""", "label or node_id is required")]
    [InlineData("get_neighbors", """{"node_id":"a","relation_filter":""}""", "relation_filter must be 1-64 characters")]
    [InlineData("get_community", """{"community_id":0}""", null)]
    [InlineData("get_community", """{"community_id":100000}""", null)]
    [InlineData("get_community", """{"community_id":-1}""", "community_id must be between 0 and 100000")]
    [InlineData("get_community", """{"community_id":100001}""", "community_id must be between 0 and 100000")]
    [InlineData("god_nodes", """{"top_n":50,"exclude_hubs_percentile":99.5}""", null)]
    [InlineData("god_nodes", """{"top_n":51}""", "top_n must be between 1 and 50")]
    [InlineData("god_nodes", """{"exclude_hubs_percentile":100.1}""", "exclude_hubs_percentile must be between 0 and 100")]
    [InlineData("shortest_path", """{"source":"a","target":"b","max_hops":12}""", null)]
    [InlineData("shortest_path", """{"source":"a","target":"b","max_hops":13}""", "max_hops must be between 1 and 12")]
    [InlineData("shortest_path", """{"source":"a"}""", "target is required")]
    [InlineData("get_pr_impact", """{"pr_number":1,"repo":"MudXtra/MudX"}""", null)]
    [InlineData("get_pr_impact", """{"pr_number":0,"repo":"MudXtra/MudX"}""", "pr_number must be between 1 and 10000000")]
    [InlineData("get_pr_impact", """{"pr_number":5}""", "repo is required")]
    [InlineData("list_prs", """{"repo":"someone/else"}""", "repo must be one of: MudBlazor/MudBlazor, MudXtra/MudX")]
    [InlineData("list_prs", """{"project_path":"MudXtra/MudX"}""", "unknown argument 'project_path' for list_prs")]
    [InlineData("triage_prs", """{"base":"dev"}""", null)]
    [InlineData("resources_read", """{}""", "unknown tool 'resources_read'")]
    public void Arguments_are_checked_against_the_advertised_bounds(string tool, string args, string? error) =>
        Assert.Equal(error, Check(tool, args));

    [Fact]
    public void Question_length_boundary_is_300()
    {
        Assert.Null(Check("query_graph", $$"""{"question":"{{new string('q', 300)}}"}"""));
        Assert.Equal("question must be 1-300 characters", Check("query_graph", $$"""{"question":"{{new string('q', 301)}}"}"""));
        Assert.Equal("base must be 1-100 characters", Check("list_prs", $$"""{"base":"{{new string('b', 101)}}"}"""));
    }
}
