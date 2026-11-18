using MudBlazor;

namespace MudMaps.City.Layout;

/// <summary>Hallmark "Kiln", ember colourway: near-black ground, cream ink, heat only where something is hot.</summary>
public static class CityTheme
{
    private static readonly string[] Display = ["Boska", "Iowan Old Style", "Georgia", "serif"];
    private static readonly string[] Body = ["Geist", "ui-sans-serif", "system-ui", "sans-serif"];

    public static readonly MudTheme Theme = new()
    {
        PaletteDark = new PaletteDark
        {
            Background = "#0b0605",
            Surface = "#170e0b",
            AppbarBackground = "#0b0605",
            DrawerBackground = "#170e0b",
            TextPrimary = "#f2eadd",
            TextSecondary = "#b3a192",
            Primary = "#fe6a00",
            PrimaryContrastText = "#100704",
            Secondary = "#d5c8b9",
            LinesDefault = "#2b2320",
            Divider = "#2b2320",
            ActionDefault = "#d5c8b9",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Body, FontSize = "1rem", LineHeight = "1.55" },
            H1 = new H1Typography { FontFamily = Display, FontSize = "4.2rem", FontWeight = "500", LineHeight = "1", LetterSpacing = "-0.02em" },
            H3 = new H3Typography { FontFamily = Display, FontSize = "3rem", FontWeight = "500", LineHeight = "1", LetterSpacing = "-0.03em" },
            H5 = new H5Typography { FontFamily = Display, FontSize = "1.5rem", FontWeight = "500", LineHeight = "1.1" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Body, FontSize = "1.125rem", LineHeight = "1.5" },
            Caption = new CaptionTypography { FontFamily = Body, FontSize = "0.875rem", FontWeight = "500" },
            Button = new ButtonTypography { FontFamily = Body, FontWeight = "500", TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "16px" },
    };
}
