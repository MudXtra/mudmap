using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MudMaps.Server.Research;

/// <summary>
/// The ten tools the research endpoints offer: graphify 0.9.108's seven graph tools and its three PR tools, with
/// graphify's names, descriptions and schemas plus a bound on every argument. The schema is also the validator, so
/// what <c>tools/list</c> advertises is exactly what <c>tools/call</c> accepts. Out-of-range values are rejected,
/// never clamped, so callers learn the limits.
/// </summary>
public sealed class ToolCatalog
{
    public static readonly IReadOnlySet<string> GraphTools = new HashSet<string>
        { "query_graph", "get_node", "get_neighbors", "get_community", "god_nodes", "graph_stats", "shortest_path" };

    public static readonly IReadOnlySet<string> PrTools = new HashSet<string> { "list_prs", "get_pr_impact", "triage_prs" };

    private readonly Dictionary<string, JsonObject> _schemas = [];

    public ToolCatalog(IEnumerable<string> repoFullNames)
    {
        var repos = new JsonArray(repoFullNames.Select(r => (JsonNode)JsonValue.Create(r)).ToArray());
        using var stream = typeof(ToolCatalog).Assembly.GetManifestResourceStream("MudMaps.Server.Research.graphify-tools.json")
            ?? throw new InvalidOperationException("graphify-tools.json is not embedded");
        var tools = JsonNode.Parse(stream)!.AsArray();
        foreach (var tool in tools)
        {
            var name = tool!["name"]!.GetValue<string>();
            var props = tool["inputSchema"]!["properties"]!.AsObject();
            if (GraphTools.Contains(name))
            {
                // graphify takes any absolute path here; this front takes only a configured repository and rewrites it.
                props["project_path"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = repos.DeepClone(),
                    ["description"] = "Repository (owner/repo) whose own graph to search. Optional — defaults to the merged graph of all mapped repositories.",
                };
            }
            else
            {
                props["repo"]!["enum"] = repos.DeepClone();
            }
            _schemas[name] = tool["inputSchema"]!.AsObject();
        }
        ToolsListResult = new JsonObject { ["tools"] = tools };
        ToolsListJson = ToolsListResult.ToJsonString();
    }

    /// <summary>The <c>tools/list</c> result, built once.</summary>
    public JsonObject ToolsListResult { get; }

    public string ToolsListJson { get; }

    public bool Has(string name) => _schemas.ContainsKey(name);

    /// <summary>Checks a <c>tools/call</c>'s arguments; returns the reason for <c>-32602</c>, or null when they are valid.</summary>
    public string? Validate(string tool, JsonObject args)
    {
        if (!_schemas.TryGetValue(tool, out var schema)) return $"unknown tool '{Clip(tool)}'";
        var props = schema["properties"]!.AsObject();
        foreach (var (key, value) in args)
        {
            if (!props.TryGetPropertyValue(key, out var prop)) return $"unknown argument '{Clip(key)}' for {tool}";
            if (CheckValue(key, value, prop!.AsObject()) is { } error) return error;
        }
        if (schema["required"] is JsonArray required)
        {
            foreach (var r in required)
            {
                var name = r!.GetValue<string>();
                if (!args.ContainsKey(name)) return $"{name} is required";
            }
        }
        // graphify answers a lookup without a name with an error; asking for one up front saves the upstream call.
        if (tool is "get_node" or "get_neighbors" && !args.ContainsKey("label") && !args.ContainsKey("node_id"))
            return "label or node_id is required";
        return null;
    }

    private static string? CheckValue(string name, JsonNode? value, JsonObject prop)
    {
        var type = prop["type"]!.GetValue<string>();
        if (value is null) return $"{name} must be a {type}";
        switch (type)
        {
            case "string":
                if (value.GetValueKind() != JsonValueKind.String) return $"{name} must be a string";
                return CheckString(name, value.GetValue<string>(), prop);
            case "integer":
                if (value.GetValueKind() != JsonValueKind.Number || !value.AsValue().TryGetValue<long>(out var i))
                    return $"{name} must be an integer";
                return CheckRange(name, i, prop);
            case "number":
                if (value.GetValueKind() != JsonValueKind.Number) return $"{name} must be a number";
                return CheckRange(name, value.GetValue<double>(), prop);
            case "boolean":
                return value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? null : $"{name} must be a boolean";
            case "array":
                if (value is not JsonArray items) return $"{name} must be an array";
                var max = prop["maxItems"]?.GetValue<int>();
                if (max is { } m && items.Count > m) return $"{name} may have at most {m} items";
                var itemSchema = prop["items"]!.AsObject();
                foreach (var item in items)
                {
                    if (CheckValue($"{name} item", item, itemSchema) is { } error) return error;
                }
                return null;
            default:
                return $"{name} has an unsupported type";
        }
    }

    private static string? CheckString(string name, string s, JsonObject prop)
    {
        if (s.Any(char.IsControl)) return $"{name} must not contain control characters";
        if (prop["enum"] is JsonArray allowed)
        {
            return allowed.Any(a => a!.GetValue<string>() == s) ? null : $"{name} must be one of: {string.Join(", ", allowed.Select(a => a!.GetValue<string>()))}";
        }
        var min = prop["minLength"]?.GetValue<int>() ?? 0;
        var max = prop["maxLength"]?.GetValue<int>();
        if (s.Length < min || (max is { } m && s.Length > m)) return $"{name} must be {min}-{max} characters";
        if (prop["pattern"]?.GetValue<string>() is { } pattern && !Regex.IsMatch(s, pattern)) return $"{name} must match {pattern}";
        return null;
    }

    private static string? CheckRange(string name, double v, JsonObject prop)
    {
        var min = prop["minimum"]?.GetValue<double>();
        var max = prop["maximum"]?.GetValue<double>();
        if ((min is { } lo && v < lo) || (max is { } hi && v > hi)) return $"{name} must be between {min} and {max}";
        return null;
    }

    private static string Clip(string s)
    {
        var clean = new string(s.Where(c => !char.IsControl(c)).Take(64).ToArray());
        return clean;
    }
}
