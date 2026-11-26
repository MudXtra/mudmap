using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MudMaps.Server.Research;

/// <summary>
/// Cleans graphify's answers before they leave the service: every string in
/// the JSON is rewritten so no host path survives, and an answer that contains anything token-shaped is refused.
/// </summary>
public sealed partial class OutputScrubber
{
    private readonly string _reposRoot;
    private readonly string _mergedGraph;
    private readonly IReadOnlySet<string> _repos;
    private readonly string? _token;

    /// <param name="token">The GitHub token's text, held in memory only to recognise it; null when there is none.</param>
    public OutputScrubber(string reposRoot, string mergedGraph, IEnumerable<string> repoFullNames, string? token)
    {
        _reposRoot = Normalize(reposRoot);
        _mergedGraph = mergedGraph.Length > 0 ? Path.GetFullPath(mergedGraph) : "";
        _repos = repoFullNames.ToHashSet(StringComparer.Ordinal);
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    private static string Normalize(string dir) => dir.Length == 0 ? "" : Path.GetFullPath(dir).TrimEnd('/');

    [GeneratedRegex(@"Graph: (\S+) \((\d+) nodes\)")]
    private static partial Regex GraphHeader();

    // An absolute path under a system or data root, up to the next space, quote or bracket. The look-behind keeps
    // repository-relative paths ("src/data/x.cs") and URLs intact.
    [GeneratedRegex(@"(?<![\w.~/:-])/(?:data|mnt|home|root|opt|tmp|srv|proc|run|usr|etc)(?=/|\b)[^\s""'<>()\[\]{},;|`]*")]
    private static partial Regex AbsolutePath();

    private static readonly string[] TokenPrefixes = ["ghp_", "github_pat_", "gho_", "ghs_"];

    /// <summary>Rewrites every string in <paramref name="node"/> in place. False when a string looked like a token.</summary>
    public bool Scrub(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (child is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        if (!Clean(s, out var cleaned)) return false;
                        obj[key] = cleaned;
                    }
                    else if (!Scrub(child)) return false;
                }
                return true;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        if (!Clean(s, out var cleaned)) return false;
                        arr[i] = cleaned;
                    }
                    else if (!Scrub(arr[i])) return false;
                }
                return true;
            default:
                return true;
        }
    }

    /// <summary>The scrubbed text, or false when it contains the token or a GitHub token prefix.</summary>
    public bool Clean(string text, out string cleaned)
    {
        cleaned = "";
        if (_token is not null && text.Contains(_token, StringComparison.Ordinal)) return false;
        foreach (var p in TokenPrefixes)
        {
            if (text.Contains(p, StringComparison.Ordinal)) return false;
        }
        var s = GraphHeader().Replace(text, m => $"Graph: {GraphLabel(m.Groups[1].Value)} ({m.Groups[2].Value} nodes)");
        if (_reposRoot.Length > 0) s = s.Replace(_reposRoot + "/", "", StringComparison.Ordinal);
        cleaned = AbsolutePath().Replace(s, "<path>");
        return true;
    }

    /// <summary>"mud (merged)" for the merged graph, "owner/repo" for a repository's own graph.</summary>
    public string GraphLabel(string graphPath)
    {
        if (_mergedGraph.Length > 0 && graphPath == _mergedGraph) return "mud (merged)";
        if (_reposRoot.Length > 0 && graphPath.StartsWith(_reposRoot + "/", StringComparison.Ordinal))
        {
            var parts = graphPath[(_reposRoot.Length + 1)..].Split('/');
            if (parts.Length >= 2 && _repos.Contains($"{parts[0]}/{parts[1]}")) return $"{parts[0]}/{parts[1]}";
        }
        return "<path>";
    }
}
