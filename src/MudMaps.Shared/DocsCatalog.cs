using System.Text.RegularExpressions;

namespace MudMaps.Shared;

/// <summary>Where each library documents itself, and how to reach one type's API page when the site has one.</summary>
public static partial class DocsCatalog
{
    /// <param name="Docs">The library's documentation or demo site.</param>
    /// <param name="ApiIndex">The site's API overview, if it has one.</param>
    /// <param name="TypePrefix">Types this library owns start with this ("Mud", "MudEx", ...).</param>
    /// <param name="TypeUrl">Builds the API page for one type, or null when the site has no per-type pages.</param>
    public sealed record Entry(string Docs, string? ApiIndex = null, string? TypePrefix = null, Func<string, string>? TypeUrl = null);

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MudBlazor/MudBlazor"] = new("https://mudblazor.com", "https://mudblazor.com/api", "Mud",
            t => $"https://mudblazor.com/api/{t}"),
        ["MudXtra/MudX"] = new("https://mudx.org", "https://mudx.org/docs/api", "MudX",
            t => $"https://mudx.org/docs/{t["MudX".Length..].ToLowerInvariant()}"),
        ["CodeBeamOrg/CodeBeam.MudBlazor.Extensions"] = new("https://mudextensions.codebeam.org/", "https://mudextensions.codebeam.org/api"),
        ["fgilde/MudBlazor.Extensions"] = new("https://www.mudex.org", "https://www.mudex.org/api", "MudEx",
            t => $"https://www.mudex.org/api/{t}"),
        ["fgilde/MudExRichTextEditor"] = new("https://fgilde.github.io/MudExRichTextEditor/"),
        ["danheron/Heron.MudCalendar"] = new("https://mudcalendar.heron.li/"),
        ["0phois/MudBlazor.StaticInput"] = new("http://mudblazor-staticinput.tryasp.net/"),
        ["MudBlazor/TryMudBlazor"] = new("https://try.mudblazor.com"),
        ["MudXtra/MudXThemeCreator"] = new("https://themes.mudx.org"),
        ["MudXtra/LottiePlayer"] = new("https://mudx.org/docs/lottie"),
        ["CodeBeamOrg/UltimateAuth"] = new("https://lab.ultimateauth.com"),
    };

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]+$")]
    private static partial Regex TypeName();

    /// <summary>The docs entry for a repository, or null to fall back to its GitHub README.</summary>
    public static Entry? For(string fullName) => Entries.GetValueOrDefault(fullName);

    /// <summary>The API page for a code item if it is one of the library's own types ("MudDataGrid" in MudBlazor), else null.</summary>
    public static string? ApiFor(string fullName, string item)
    {
        if (For(fullName) is not { TypePrefix: { } prefix, TypeUrl: { } url }) return null;
        var name = item.TrimEnd('(', ')').Split('.')[^1];
        var generic = name.IndexOf('<');
        if (generic > 0) name = name[..generic];
        if (!TypeName().IsMatch(name) || !name.StartsWith(prefix, StringComparison.Ordinal) || name.Length <= prefix.Length) return null;
        if (name.EndsWith("Tests", StringComparison.Ordinal) || name.EndsWith("Test", StringComparison.Ordinal)) return null;
        return url(name);
    }
}
