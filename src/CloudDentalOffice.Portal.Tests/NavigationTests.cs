using System.Text.RegularExpressions;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// Navigation and page chrome: every link in the menu and on Home goes to a page that exists,
/// and staff pages use the shared PageHeader (the page's one h1, which FocusOnNavigate targets).
/// </summary>
public class NavigationTests
{
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

    private static IEnumerable<string> Pages()
        => Directory.EnumerateFiles(Path.Combine(PortalDir, "Pages"), "*.razor", SearchOption.AllDirectories);

    private static HashSet<string> Routes()
        => Pages()
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "^@page \"([^\"{]+)", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value.TrimEnd('/')))
            .Select(r => r.Length == 0 ? "/" : r)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("Shared/MainLayout.razor")]
    [InlineData("Pages/Index.razor")]
    public void Every_link_goes_to_a_page_that_exists(string file)
    {
        var routes = Routes();
        var dead = Regex.Matches(File.ReadAllText(Path.Combine(PortalDir, file)), "Href=\"(/[^\"@]*)\"")
            .Select(m => m.Groups[1].Value)
            .Where(href => !routes.Contains(href.Length > 1 ? href.TrimEnd('/') : href))
            .ToList();

        Assert.Empty(dead);
    }

    [Fact]
    public void Staff_pages_use_the_shared_page_header()
    {
        // Sign-in, sign-up, the patient's own billing page and checkout returns have their own layouts.
        var exempt = new[] { "Login.razor", "Register.razor", "Signup.razor", "PatientBilling.razor", "PaymentReturn.razor", "ClaimWizard.razor" };
        var missing = Pages()
            .Where(f => File.ReadAllText(f).Contains("@page \""))
            .Where(f => !exempt.Contains(Path.GetFileName(f)))
            .Where(f => !File.ReadAllText(f).Contains("<PageHeader "))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Page_titles_reach_the_browser_tab()
    {
        var host = File.ReadAllText(Path.Combine(PortalDir, "Pages", "_Host.cshtml"));

        Assert.Contains("typeof(HeadOutlet)", host);
    }
}
