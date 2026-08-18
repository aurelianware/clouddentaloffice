namespace Zocdoc.IntegrationTests;

public sealed class ZocdocSandboxFactAttribute : FactAttribute
{
    public ZocdocSandboxFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZOCDOC_SANDBOX_CLIENT_ID")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZOCDOC_SANDBOX_CLIENT_SECRET")))
            Skip = "Set ZOCDOC_SANDBOX_CLIENT_ID and ZOCDOC_SANDBOX_CLIENT_SECRET to run Zocdoc certification tests.";
    }
}
