using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MudMaps.Server.Maps;

namespace MudMaps.Server.Research;

/// <summary>
/// Turns graphify's text answers into links for the research panel: every source file it names becomes a GitHub link
/// and, when the file belongs to a map room, a link into City and a hit for the map lens. Reads the graphs the map has
/// already loaded; never reloads one.
/// </summary>
public sealed partial class LinkExtractor(IRepoGraphs graphs, IOptions<MapsOptions> maps)
{
    public const int MaxLinks = 60;

    // query_graph:   NODE MudDataGrid [src=src/A.razor.cs loc=L29 community=MudDataGrid]
    [GeneratedRegex(@"^NODE (?<label>.+?) \[src=(?<file>\S+) loc=(?:L(?<line>\d+))?", RegexOptions.Multiline)]
    private static partial Regex NodeLine();

    // get_node:      Source: src/MudBlazor/TScripts/mudPopover.ts L916
    [GeneratedRegex(@"^\s*Source: (?<file>\S+)(?: L(?<line>\d+))?\s*$", RegexOptions.Multiline)]
    private static partial Regex SourceLine();

    [GeneratedRegex(@"^Node: (?<label>.+)$", RegexOptions.Multiline)]
    private static partial Regex NodeName();

    // get_node:      ID: MudBlazor::src_mudblazor_...   (merged graphs prefix the repository)
    [GeneratedRegex(@"^\s*ID: (?<repo>[^:\s]+)::", RegexOptions.Multiline)]
    private static partial Regex IdPrefix();

    // get_neighbors: --> Palette [references] [EXTRACTED] at=src/A.razor.cs:L16
    [GeneratedRegex(@"^\s*(?:-->|<--|---) (?<label>.+?) \[.*? at=(?<file>[^\s:]+):L(?<line>\d+)", RegexOptions.Multiline)]
    private static partial Regex NeighborLine();

    // get_community: "  ThemePaletteColor [src/A.cs]"
    [GeneratedRegex(@"^  (?<label>[^\[\n]+?) \[(?<file>[^\]\s]+)\]\s*$", RegexOptions.Multiline)]
    private static partial Regex MemberLine();

    // Any tool, when a label names several nodes (the common case for a one-word search):
    //   Ambiguous: 'date' matches 11 nodes in different files.
    //     MudBlazor/src/MudBlazor/Enums/InputType.cs          (merged graphs prefix the clone folder)
    //       id: MudBlazor::src_mudblazor_enums_inputtype_...
    [GeneratedRegex(@"^Ambiguous: '(?<label>.+?)' matches", RegexOptions.Multiline)]
    private static partial Regex AmbiguousHead();

    [GeneratedRegex(@"^  (?<file>\S+)\r?\n    id: (?:(?<tag>[^:\s]+)::)?\S+\s*$", RegexOptions.Multiline)]
    private static partial Regex Candidate();

    /// <param name="Repo">The repository the line itself names, when it does (merged-graph candidates).</param>
    private sealed record Hit(string Label, string File, int? Line, string? Repo = null);

    public (JsonArray Links, JsonArray Rooms) Extract(string text, string? scopeRepo)
    {
        var hits = new List<Hit>();
        foreach (Match m in NodeLine().Matches(text)) hits.Add(new(m.Groups["label"].Value, m.Groups["file"].Value, Line(m)));
        var nodeName = NodeName().Match(text) is { Success: true } n ? n.Groups["label"].Value.Trim() : null;
        foreach (Match m in SourceLine().Matches(text)) hits.Add(new(nodeName ?? FileName(m.Groups["file"].Value), m.Groups["file"].Value, Line(m)));
        foreach (Match m in NeighborLine().Matches(text)) hits.Add(new(m.Groups["label"].Value, m.Groups["file"].Value, Line(m)));
        foreach (Match m in MemberLine().Matches(text)) hits.Add(new(m.Groups["label"].Value.Trim(), m.Groups["file"].Value, null));
        if (AmbiguousHead().Match(text) is { Success: true } amb)
        {
            foreach (Match m in Candidate().Matches(text))
            {
                var file = m.Groups["file"].Value;
                var tag = m.Groups["tag"].Success ? m.Groups["tag"].Value : null;
                var repo = tag is null ? null : ByTag(tag);
                // A merged graph names the file under its clone folder; the map and GitHub use the repo-relative path.
                if (tag is not null && file.StartsWith(tag + "/", StringComparison.Ordinal)) file = file[(tag.Length + 1)..];
                hits.Add(new(amb.Groups["label"].Value, file, null, repo));
            }
        }

        var idRepo = IdPrefix().Match(text) is { Success: true } id ? ByTag(id.Groups["repo"].Value) : null;
        var links = new JsonArray();
        var seen = new HashSet<(string, string, int?)>();
        var rooms = new Dictionary<(string Repo, string Room), int>();
        foreach (var hit in hits)
        {
            var repo = scopeRepo ?? hit.Repo ?? idRepo ?? RepoOwning(hit.File);
            if (repo is null || !seen.Add((repo, hit.File, hit.Line))) continue;
            var room = graphs.Graph(repo)?.FileRooms.GetValueOrDefault(hit.File)?.FirstOrDefault();
            if (room is not null) rooms[(repo, room)] = rooms.GetValueOrDefault((repo, room)) + 1;
            if (links.Count >= MaxLinks) continue;
            var link = new JsonObject { ["label"] = hit.Label, ["repo"] = repo, ["file"] = hit.File };
            if (hit.Line is { } line) link["line"] = line;
            if (room is not null)
            {
                link["roomId"] = room;
                link["href"] = $"../city/?repo={Uri.EscapeDataString(CityName(repo))}&room={Uri.EscapeDataString(room)}";
            }
            link["github"] = GitHubUrl(repo, hit.File, hit.Line);
            links.Add(link);
        }
        var roomList = new JsonArray(rooms.OrderByDescending(kv => kv.Value)
            .Select(kv => (JsonNode)new JsonObject { ["repo"] = kv.Key.Repo, ["roomId"] = kv.Key.Room, ["count"] = kv.Value }).ToArray());
        return (links, roomList);
    }

    public static string GitHubUrl(string repo, string file, int? line) =>
        $"https://github.com/{repo}/blob/HEAD/{string.Join('/', file.Split('/').Select(Uri.EscapeDataString))}" + (line is { } l ? $"#L{l}" : "");

    private static int? Line(Match m) => m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out var l) ? l : null;

    private static string FileName(string file) => file.Split('/')[^1];

    /// <summary>City's <c>?repo=</c> takes the repository's display name (the hood's Repo).</summary>
    private string CityName(string fullName) =>
        maps.Value.Repos.FirstOrDefault(r => r.FullName == fullName)?.Name is { Length: > 0 } name ? name : fullName;

    /// <summary>A merged graph's repository tag ("MudBlazor") is the clone's folder name, i.e. the repo part of owner/repo.</summary>
    private string? ByTag(string tag)
    {
        var matches = maps.Value.Repos.Where(r => r.FullName.Split('/')[^1] == tag).ToList();
        return matches.Count == 1 ? matches[0].FullName : null;
    }

    /// <summary>The one configured repository whose graph has this file; null when none or several do.</summary>
    private string? RepoOwning(string file)
    {
        string? found = null;
        foreach (var r in maps.Value.Repos)
        {
            if (graphs.Graph(r.FullName)?.FileCommunities.ContainsKey(file) != true) continue;
            if (found is not null) return null;
            found = r.FullName;
        }
        return found;
    }
}
