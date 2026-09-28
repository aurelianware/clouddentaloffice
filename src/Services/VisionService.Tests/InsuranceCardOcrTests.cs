using System.Net.Http.Json;
using CloudDentalOffice.Contracts.Vision;
using VisionService.Adapters;

/// <summary>Card OCR: the read response is parsed as JSON, and a mock read says it's made up.</summary>
public sealed class InsuranceCardOcrTests(VisionSecurityFactory factory) : IClassFixture<VisionSecurityFactory>
{
    [Fact]
    public void Read_response_lines_come_out_in_order_including_escaped_characters()
    {
        const string json = """
            {"modelVersion":"2023-10-01","readResult":{"blocks":[
              {"lines":[{"text":"DELTA DENTAL","boundingPolygon":[]},{"text":"Member ID: \"AB123\\45\""}]},
              {"lines":[{"text":"Group: 5500"}]},
              {"lines":[]}]}}
            """;

        Assert.Equal("DELTA DENTAL\nMember ID: \"AB123\\45\"\nGroup: 5500",
            AzureAiVisionOcrGateway.ExtractTextFromReadResponse(json));
        Assert.Equal("", AzureAiVisionOcrGateway.ExtractTextFromReadResponse("""{"readResult":{}}"""));
    }

    [Fact]
    public async Task A_mock_scan_is_marked_simulated_and_triggers_no_check()
    {
        var response = await factory.StaffClient("tenant-a").PostAsJsonAsync("/api/vision/insurance/scan",
            new ScanInsuranceCardRequest { ImageBase64 = Convert.ToBase64String([1, 2, 3]) });

        response.EnsureSuccessStatusCode();
        var scan = await response.Content.ReadFromJsonAsync<InsuranceCardScanDto>();
        Assert.True(scan!.Simulated);
        Assert.False(scan.EligibilityCheckTriggered);
    }
}
