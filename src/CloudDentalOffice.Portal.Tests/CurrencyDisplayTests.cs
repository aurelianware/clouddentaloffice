using CloudDentalOffice.Portal.Components;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;
using MudBlazor.Services;

namespace CloudDentalOffice.Portal.Tests;

public class CurrencyDisplayTests
{
    [Theory]
    [InlineData(1000, "$1,000.00")]
    [InlineData(0, "$0.00")]
    [InlineData(-12.5, "-$12.50")]
    public void Usd_does_not_require_an_installed_culture(decimal amount, string expected) =>
        Assert.Equal(expected, CurrencyDisplay.Usd(amount));

    [Fact]
    public async Task Eligibility_result_with_money_renders_in_the_production_globalization_mode()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime>(new Mock<IJSRuntime>().Object);
        services.AddSingleton<NavigationManager>(new TestNavigation());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = new EligibilityResult
            {
                CorrelationId="synthetic", Source="Synthetic test", CoverageStatus=CoverageStatus.Active,
                AnnualMaximum=1000m, Deductible=100m,
                Benefits=[new EligibilityBenefit("40", "Oral surgery", CoverageStatus.Active, Coinsurance:0.4m, Limit:1000m)]
            };
            var output = await renderer.RenderComponentAsync<EligibilityResultView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(EligibilityResultView.Result)] = result }));
            var html = output.ToHtmlString();
            Assert.Contains("$1,000.00", html);
            Assert.Contains("$100.00", html);
        });
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://example.test/", "https://example.test/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
