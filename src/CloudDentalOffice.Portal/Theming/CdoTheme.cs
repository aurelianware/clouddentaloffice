using MudBlazor;

namespace CloudDentalOffice.Portal.Theming;

/// <summary>
/// The portal's MudBlazor theme. It mirrors wwwroot/css/theme-tokens.css: MudBlazor paints
/// its own components from this palette, and page styles paint from the CSS variables, so keep
/// the hex values here in step with that file. The palette is Cloud Health Office's Enterprise
/// theme, so the two products look like one family.
/// </summary>
public static class CdoTheme
{
    public const string Accent = "#0E7490";
    public const string Navy = "#0B2545";
    public const string Ink = "#0B1F33";
    public const string Canvas = "#F5F7FA";
    public const string Surface = "#FFFFFF";
    public const string Raised = "#EEF3F8";
    public const string Line = "#D9E1EA";
    public const string LineStrong = "#B8C4D2";
    public const string Text = "#334155";
    public const string TextMuted = "#475569";

    public const string Success = "#15803D";
    public const string Warning = "#B45309";
    public const string Error = "#B91C1C";
    public const string Info = "#0369A1";

    private static readonly string[] Fonts =
        { "IBM Plex Sans", "system-ui", "-apple-system", "Segoe UI", "Roboto", "sans-serif" };

    public static MudTheme Create() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = Accent,
            PrimaryContrastText = "#FFFFFF",
            Secondary = Navy,
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#0F766E",
            TertiaryContrastText = "#FFFFFF",
            Info = Info,
            InfoContrastText = "#FFFFFF",
            Success = Success,
            SuccessContrastText = "#FFFFFF",
            Warning = Warning,
            WarningContrastText = "#FFFFFF",
            Error = Error,
            ErrorContrastText = "#FFFFFF",
            Dark = Ink,

            Background = Canvas,
            BackgroundGray = Raised,
            Surface = Surface,
            DrawerBackground = Surface,
            DrawerText = Text,
            DrawerIcon = TextMuted,
            AppbarBackground = Navy,
            AppbarText = "#FFFFFF",

            TextPrimary = Ink,
            TextSecondary = TextMuted,
            TextDisabled = "#94A3B8",

            ActionDefault = TextMuted,
            ActionDisabled = "rgba(11, 31, 51, 0.26)",
            ActionDisabledBackground = "rgba(11, 31, 51, 0.08)",
            HoverOpacity = 0.06,

            LinesDefault = Line,
            LinesInputs = LineStrong,
            TableLines = Line,
            TableStriped = Canvas,
            TableHover = "#E6F2F5",
            Divider = Line,
            DividerLight = Raised,
        },
        Typography = new Typography
        {
            Default = new Default { FontFamily = Fonts, FontSize = "0.875rem", LineHeight = 1.5 },
            H1 = new H1 { FontFamily = Fonts, FontWeight = 700 },
            H2 = new H2 { FontFamily = Fonts, FontWeight = 700 },
            H3 = new H3 { FontFamily = Fonts, FontWeight = 700, FontSize = "1.75rem" },
            H4 = new H4 { FontFamily = Fonts, FontWeight = 700, FontSize = "1.5rem" },
            H5 = new H5 { FontFamily = Fonts, FontWeight = 600, FontSize = "1.25rem" },
            H6 = new H6 { FontFamily = Fonts, FontWeight = 600, FontSize = "1.05rem" },
            Button = new Button { FontFamily = Fonts, FontWeight = 600, TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "6px" },
    };
}
