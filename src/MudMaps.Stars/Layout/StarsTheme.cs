using MudBlazor;

namespace MudMaps.Stars.Layout;

/// <summary>From the ui-ux-pro-max design system: dark code, star gold, fork silver, sponsor purple; Fira Sans and Fira Code.</summary>
public static class StarsTheme
{
    private static readonly string[] Sans = ["Fira Sans", "ui-sans-serif", "system-ui", "sans-serif"];
    private static readonly string[] Mono = ["Fira Code", "ui-monospace", "monospace"];

    public static readonly MudTheme Theme = new()
    {
        PaletteDark = new PaletteDark
        {
            Background = "#020617",
            Surface = "#0E1223",
            AppbarBackground = "#020617",
            DrawerBackground = "#0E1223",
            TextPrimary = "#F8FAFC",
            TextSecondary = "#94A3B8",
            Primary = "#FACC15",
            PrimaryContrastText = "#020617",
            Secondary = "#A78BFA",
            LinesDefault = "#334155",
            Divider = "#334155",
            ActionDefault = "#CBD5E1",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Sans, FontSize = "1rem", LineHeight = "1.55" },
            H1 = new H1Typography { FontFamily = Sans, FontSize = "4.4rem", FontWeight = "300", LineHeight = "1", LetterSpacing = "-0.03em" },
            H3 = new H3Typography { FontFamily = Mono, FontSize = "2rem", FontWeight = "500", LineHeight = "1.1" },
            H5 = new H5Typography { FontFamily = Sans, FontSize = "1.4rem", FontWeight = "600", LineHeight = "1.15" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Sans, FontSize = "1.0625rem", LineHeight = "1.55" },
            Caption = new CaptionTypography { FontFamily = Sans, FontSize = "0.875rem", FontWeight = "400" },
            Button = new ButtonTypography { FontFamily = Sans, FontWeight = "500", TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "12px" },
    };
}
