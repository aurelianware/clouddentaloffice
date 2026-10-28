using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using CloudDentalOffice.Portal.Theming;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// Pages paint from the CSS tokens in theme-tokens.css and MudBlazor paints from CdoTheme.
/// These tests keep the two in step, keep the palette readable, and stop hardcoded colors
/// (the old Sentinel neon in particular) coming back into pages.
/// </summary>
public class ThemeTokenTests
{
    private const double AaText = 4.5;
    private static readonly string PortalDir = FindPortalDir();

    private static string FindPortalDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CloudDentalOffice.Portal");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("src/CloudDentalOffice.Portal");
    }

    private static string Css(string name) => File.ReadAllText(Path.Combine(PortalDir, "wwwroot", "css", name));

    private static Dictionary<string, string> Tokens()
        => Regex.Matches(Css("theme-tokens.css"), @"(--cdo-[a-z0-9-]+)\s*:\s*([^;]+);")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());

    private static IEnumerable<string> PortalSources(params string[] extensions)
        => Directory.EnumerateFiles(PortalDir, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.Ordinal)))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    [Theory]
    [InlineData("--cdo-ink")]
    [InlineData("--cdo-text")]
    [InlineData("--cdo-text-muted")]
    [InlineData("--cdo-text-subtle")]
    [InlineData("--cdo-accent")]
    [InlineData("--cdo-accent-hover")]
    [InlineData("--cdo-accent-2")]
    [InlineData("--cdo-success")]
    [InlineData("--cdo-warning")]
    [InlineData("--cdo-error")]
    [InlineData("--cdo-info")]
    public void Text_colors_meet_WCAG_AA_on_cards_and_canvas(string token)
    {
        var tokens = Tokens();

        Assert.True(Contrast(tokens[token], tokens["--cdo-surface"]) >= AaText, $"{token} on --cdo-surface");
        Assert.True(Contrast(tokens[token], tokens["--cdo-bg"]) >= AaText, $"{token} on --cdo-bg");
    }

    [Theory]
    [InlineData("success")]
    [InlineData("warning")]
    [InlineData("error")]
    [InlineData("info")]
    public void Status_text_is_readable_on_its_own_soft_background(string status)
    {
        var tokens = Tokens();

        Assert.True(Contrast(tokens[$"--cdo-{status}"], tokens[$"--cdo-{status}-soft"]) >= AaText);
    }

    [Fact]
    public void App_bar_text_is_readable_on_the_navy_bar()
    {
        var tokens = Tokens();

        Assert.True(Contrast(tokens["--cdo-on-appbar"], tokens["--cdo-appbar"]) >= AaText);
        Assert.True(Contrast(tokens["--cdo-on-appbar-muted"], tokens["--cdo-appbar"]) >= AaText);
    }

    [Fact]
    public void Every_referenced_token_is_defined()
    {
        var defined = Tokens().Keys.ToHashSet();
        var missing = PortalSources(".razor", ".cs", ".cshtml", ".css")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"var\((--cdo-[a-z0-9-]+)\)")
                .Select(m => $"{Path.GetFileName(f)}: {m.Groups[1].Value}"))
            .Where(r => !defined.Contains(r[(r.IndexOf(' ') + 1)..]))
            .Distinct()
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Pages_do_not_hardcode_colors()
    {
        // Colors belong in theme-tokens.css. The exceptions are data, not styling: the
        // chart service's procedure colors and two categorical detection colors.
        var allowed = new[] { "ClinicalChartService.cs", "IClinicalChartService.cs", "VisionEventCard.razor" };
        var offenders = PortalSources(".razor", ".razor.css")
            .Where(f => !allowed.Contains(Path.GetFileName(f)))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"#[0-9a-fA-F]{6}\b|#[0-9a-fA-F]{3}\b|rgba?\(")
                .Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("--cdo-accent", nameof(CdoTheme.Accent))]
    [InlineData("--cdo-appbar", nameof(CdoTheme.Navy))]
    [InlineData("--cdo-ink", nameof(CdoTheme.Ink))]
    [InlineData("--cdo-bg", nameof(CdoTheme.Canvas))]
    [InlineData("--cdo-surface", nameof(CdoTheme.Surface))]
    [InlineData("--cdo-raised", nameof(CdoTheme.Raised))]
    [InlineData("--cdo-line", nameof(CdoTheme.Line))]
    [InlineData("--cdo-line-strong", nameof(CdoTheme.LineStrong))]
    [InlineData("--cdo-text", nameof(CdoTheme.Text))]
    [InlineData("--cdo-text-muted", nameof(CdoTheme.TextMuted))]
    [InlineData("--cdo-success", nameof(CdoTheme.Success))]
    [InlineData("--cdo-warning", nameof(CdoTheme.Warning))]
    [InlineData("--cdo-error", nameof(CdoTheme.Error))]
    [InlineData("--cdo-info", nameof(CdoTheme.Info))]
    public void MudBlazor_theme_matches_the_css_tokens(string token, string constant)
    {
        var value = (string)typeof(CdoTheme).GetField(constant, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        Assert.Equal(Tokens()[token], value, ignoreCase: true);
    }

    [Fact]
    public void MudBlazor_palette_uses_the_theme_colors()
    {
        var palette = CdoTheme.Create().PaletteLight;

        Assert.Equal(CdoTheme.Accent, palette.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex), ignoreCase: true);
        Assert.Equal(CdoTheme.Navy, palette.AppbarBackground.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex), ignoreCase: true);
        Assert.Equal(CdoTheme.Canvas, palette.Background.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex), ignoreCase: true);
    }

    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        hex = hex.TrimStart('#');
        double Channel(int i)
        {
            var c = int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }
}
