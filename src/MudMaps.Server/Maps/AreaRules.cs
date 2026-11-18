using System.Text.RegularExpressions;

namespace MudMaps.Server.Maps;

/// <summary>Turns repository paths into map areas. Kept in step with tools/build_data.py.</summary>
public static partial class AreaRules
{
    public static readonly IReadOnlySet<string> Generic = new HashSet<string>
    {
        "Task", "List", "IEnumerable", "IReadOnlyList", "Action", "Type", "Fact", "TestCase", "Test",
        "XElement", "CultureInfo", "CancellationToken", "RenderFragment", "IJSRuntime", "IRenderedComponent",
        "string", "int", "bool", "object", "EventCallback", "Func", "Dictionary", "ValueTask",
    };

    private static readonly Dictionary<string, string> Plurals = new()
    {
        ["Charts"] = "Chart", ["Dialogs"] = "Dialog", ["Pickers"] = "Picker", ["Inputs"] = "Input",
        ["Buttons"] = "Button", ["Menus"] = "Menu", ["Lists"] = "List",
    };

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["Docs"] = "Docs & samples", ["Core"] = "Core", ["Unit"] = "Unit tests", ["TScripts"] = "TypeScript",
    };

    [GeneratedRegex(@"(\.min\.(js|css)$|/lib/|node_modules/|/wwwroot/(?!.*\.(ts|razor)$)|\.resx$|/Translations?/|\.ya?ml$|/bin/|/obj/|\.github/|\.json$|\.md$|\.svg$|\.map$|/dist/)", RegexOptions.IgnoreCase)]
    private static partial Regex Junk();

    [GeneratedRegex(@"\.(razor\.cs|razor|cs|ts|js|scss|css)$")]
    private static partial Regex Extension();

    [GeneratedRegex(@"(Tests?|Examples?|Page)$")]
    private static partial Regex Suffix();

    [GeneratedRegex(@"^(MudEx|MudX|Mud)(?=[A-Z])")]
    private static partial Regex MudPrefix();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex WordBreak();

    /// <summary>"MudDataGridTests.cs" becomes "DataGrid".</summary>
    public static string Norm(string name)
    {
        name = Extension().Replace(name, "");
        name = Suffix().Replace(name, "");
        name = MudPrefix().Replace(name, "");
        if (name.Contains('.')) return "Core";
        if (Plurals.TryGetValue(name, out var single)) return single;
        return name.Length == 0 ? "Core" : name;
    }

    /// <summary>The folder-based area of a repository-relative path, or null for noise.</summary>
    public static string? AreaOf(string? path)
    {
        if (string.IsNullOrEmpty(path) || Junk().IsMatch(path)) return null;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "Core";
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var low = parts[i].ToLowerInvariant();
            if (low is "components" or "testcomponents") return Norm(parts[i + 1]);
        }
        var project = parts[0].ToLowerInvariant() is "src" or "source" && parts.Length > 2 ? parts[1] : parts[0];
        var p = project.ToLowerInvariant();
        if (p.Contains("docs") || p.Contains("website") || p.Contains("sample")) return "Docs";
        if (p.Contains("analyzer")) return "Analyzers";
        var rest = parts[(Array.IndexOf(parts, project) + 1)..];
        return rest.Length >= 2 ? Norm(rest[0]) : "Core";
    }

    public static string Label(string key) => Labels.TryGetValue(key, out var l) ? l : WordBreak().Replace(key, " ");

    public static string Slug(string name) => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}
