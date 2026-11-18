using MudBlazor;

namespace MudMaps.Colony.Layout;

/// <summary>Hallmark "Spore": a warm cellar at night, parchment ink, one oyster-pink signal.</summary>
public static class ColonyTheme
{
    public const string Paper = "#0f0a06";
    public const string Paper2 = "#19120e";
    public const string Paper3 = "#271d17";
    public const string Ink = "#efe7d8";
    public const string Ink2 = "#d3c9bb";
    public const string Muted = "#9d9388";
    public const string Rule = "#372f28";
    public const string Accent = "#e89f92";

    private static readonly string[] Display = ["Young Serif", "Georgia", "serif"];
    private static readonly string[] Body = ["Instrument Sans", "ui-sans-serif", "system-ui", "sans-serif"];

    public static readonly MudTheme Theme = new()
    {
        PaletteDark = new PaletteDark
        {
            Background = Paper,
            Surface = Paper2,
            AppbarBackground = Paper,
            DrawerBackground = Paper2,
            TextPrimary = Ink,
            TextSecondary = Muted,
            Primary = Accent,
            PrimaryContrastText = Paper,
            Secondary = Ink2,
            LinesDefault = Rule,
            Divider = Rule,
            ActionDefault = Ink2,
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Body, FontSize = "1rem", LineHeight = "1.55" },
            H1 = new H1Typography { FontFamily = Display, FontSize = "4.25rem", FontWeight = "400", LineHeight = "1.02", LetterSpacing = "-0.012em" },
            H3 = new H3Typography { FontFamily = Display, FontSize = "2.75rem", FontWeight = "400", LineHeight = "1" },
            H5 = new H5Typography { FontFamily = Display, FontSize = "1.5rem", FontWeight = "400", LineHeight = "1.1" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Body, FontSize = "1.125rem", LineHeight = "1.5" },
            Caption = new CaptionTypography { FontFamily = Body, FontSize = "0.875rem", FontWeight = "500" },
            Button = new ButtonTypography { FontFamily = Body, FontWeight = "500", TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "14px" },
    };
}
