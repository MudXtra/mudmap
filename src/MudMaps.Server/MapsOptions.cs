namespace MudMaps.Server;

/// <summary>Configuration for the map service. Every path and repository is fixed here; nothing comes from requests.</summary>
public sealed class MapsOptions
{
    public const string Section = "Maps";

    /// <summary>Root of the read-only reference clones, each with graphify-out/graph.json.</summary>
    public string ReposRoot { get; set; } = "repos";

    /// <summary>Path the service is mounted under behind the proxy, e.g. "/mudmap". Empty serves from the root.</summary>
    public string PathBase { get; set; } = "";

    /// <summary>Origins allowed to show the maps in an iframe, besides the service's own origin.</summary>
    public List<string> FrameAncestors { get; set; } = [];

    /// <summary>Old URL segments that redirect to their new names, e.g. "stars" -> "space".</summary>
    public Dictionary<string, string> Aliases { get; set; } = [];

    public List<RepoOption> Repos { get; set; } =
    [
        new("MudBlazor/MudBlazor", "MudBlazor", 24),
        new("fgilde/MudBlazor.Extensions", "MudBlazor.Extensions", 14),
        new("CodeBeamOrg/CodeBeam.MudBlazor.Extensions", "CodeBeam Extensions", 10),
        new("MudXtra/MudX", "MudX", 6),
        new("MudBlazor/TryMudBlazor", "TryMudBlazor", 6),
        new("danheron/Heron.MudCalendar", "Heron Calendar", 5),
        new("MudBlazor/Templates", "Templates", 4),
        new("0phois/MudBlazor.StaticInput", "Static Inputs", 4),
        new("MudBlazor/ThemeManager", "ThemeManager", 3),
        new("CodeBeamOrg/UltimateAuth", "UltimateAuth", 6),
        new("fgilde/MudExRichTextEditor", "MudEx RichTextEditor", 4),
        new("MudXtra/MudXThemeCreator", "ThemeCreator", 4),
        new("MudXtra/MudXtra.CodeAnalysis", "CodeAnalysis", 3),
        new("MudXtra/LottiePlayer", "LottiePlayer", 3),
        new("MudBlazor/MudBlazor.Icons", "Icons", 2),
    ];

    public int WindowDays { get; set; } = 30;

    /// <summary>How often to check the graph files for a newer version.</summary>
    public int GraphPollSeconds { get; set; } = 60;

    /// <summary>How often to ask GitHub for pull request activity.</summary>
    public int PullRequestRefreshMinutes { get; set; } = 10;

    /// <summary>File holding a GitHub token (read-only public access is enough). Missing file: no live PR data.</summary>
    public string? GitHubTokenFile { get; set; }

    /// <summary>Published map apps to host, by URL segment.</summary>
    public Dictionary<string, string> Sites { get; set; } = [];

    public sealed class RepoOption
    {
        public RepoOption() { }

        public RepoOption(string fullName, string name, int areaBudget)
        {
            FullName = fullName;
            Name = name;
            AreaBudget = areaBudget;
        }

        public string FullName { get; set; } = "";
        public string Name { get; set; } = "";
        public int AreaBudget { get; set; }
    }
}
