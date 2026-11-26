using MudBlazor;

namespace MudMaps.Islands.Layout;

/// <summary>design-taste "retro-print": a two-ink risograph sea chart. Paper, ink, riso red, riso blue; square corners.</summary>
public static class IslandsTheme
{
    public const string Paper = "#F4EDDE";
    public const string Paper2 = "#EBE2CF";
    public const string Ink = "#21201C";
    public const string Red = "#E4572E";

    /// <summary>The riso red darkened for text and controls: 5.9:1 on the paper, where the plain red is 3:1.</summary>
    public const string RedInk = "#A3321A";
    public const string Blue = "#355E8C";

    private static readonly string[] Display = ["Anton", "Impact", "sans-serif"];
    private static readonly string[] Serif = ["Newsreader", "Georgia", "serif"];
    private static readonly string[] Mono = ["IBM Plex Mono", "ui-monospace", "monospace"];

    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Background = Paper,
            Surface = Paper,
            AppbarBackground = Paper,
            TextPrimary = Ink,
            TextSecondary = "#4a463d",
            Primary = RedInk,
            PrimaryContrastText = Paper,
            Secondary = Blue,
            LinesDefault = Ink,
            Divider = Ink,
            ActionDefault = Ink,
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Serif, FontSize = "1.0625rem", LineHeight = "1.65" },
            H1 = new H1Typography { FontFamily = Display, FontSize = "5.5rem", FontWeight = "400", LineHeight = ".95", TextTransform = "uppercase" },
            H3 = new H3Typography { FontFamily = Display, FontSize = "2.6rem", FontWeight = "400", LineHeight = "1" },
            H5 = new H5Typography { FontFamily = Display, FontSize = "1.6rem", FontWeight = "400", LineHeight = "1.05", LetterSpacing = ".01em" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Serif, FontSize = "1.125rem", LineHeight = "1.6" },
            Caption = new CaptionTypography { FontFamily = Mono, FontSize = "0.8125rem", FontWeight = "500", LetterSpacing = ".02em" },
            Button = new ButtonTypography { FontFamily = Mono, FontSize = ".8125rem", FontWeight = "500", LetterSpacing = ".02em", TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "0" },
        Shadows = new Shadow(),
    };
}
