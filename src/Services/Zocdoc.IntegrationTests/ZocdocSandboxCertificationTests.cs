using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Zocdoc.IntegrationTests;

/// <summary>
/// Read-only checks against Zocdoc's documented predefined sandbox records.
/// Stateful booking/action and mock-webhook exercises remain partner-assisted
/// checklist steps because they mutate the shared sandbox and require a callback URL.
/// </summary>
public sealed class ZocdocSandboxCertificationTests
{
    private static readonly (string Id, string Status, string? PatientType)[] AppointmentCases =
    [
        ("2b29f79b-6d7f-472a-9603-d0c378bc9531", "pending_booking", null),
        ("d2ee5bd8-643a-42c8-8c5a-be450e903430", "confirmed", "new"),
        ("423e6a11-8dac-4873-b933-d8d02f9a370f", "confirmed", "existing"),
        ("34e4ead3-ca69-4448-9438-58702dd1048f", "booking_failed", null),
        ("21990114-ea71-4d7d-9d1e-00c43ae44bcd", "cancelled", null),
        ("a0a7770d-e667-416c-9f06-9c3b40a7bb84", "no_show", null),
        ("63f995c2-49c4-40c8-a93a-140fb32e913b", "pending_reschedule", null),
        ("8507d05f-cbe5-4732-b72a-22add9c80120", "rescheduled", null),
        ("84d04f67-b2cf-4afd-ab64-193072498ed5", "reschedule_failed", null)
    ];

    [ZocdocSandboxFact]
    public async Task OAuthAndReferenceDataAreReachable()
    {
        using var client = new ZocdocSandboxClient();
        await client.AuthenticateAsync();
        using var visitReasons = await client.GetAsync("v1/visit_reasons?page_size=1");
        Assert.Equal(JsonValueKind.Array, visitReasons.RootElement.GetProperty("data").ValueKind);
    }

    [ZocdocSandboxFact]
    public async Task MultipleLocationProviderScenarioIsAvailable()
    {
        using var client = new ZocdocSandboxClient();
        await client.AuthenticateAsync();
        using var providers = await client.GetAsync("v1/providers?npis=npi_multiplelocations");
        Assert.True(providers.RootElement.GetProperty("data").GetArrayLength() > 1);
    }

    [ZocdocSandboxFact]
    public async Task DocumentedAppointmentLifecycleScenariosMatchExpectedStatus()
    {
        using var client = new ZocdocSandboxClient();
        await client.AuthenticateAsync();
        foreach (var (id, status, patientType) in AppointmentCases)
        {
            using var appointment = await client.GetAsync($"v1/appointments/{id}");
            Assert.Equal(status, appointment.RootElement.GetProperty("status").GetString());
            if (patientType is not null)
                Assert.Equal(patientType, appointment.RootElement.GetProperty("patient_type").GetString());
        }
    }

    [ZocdocSandboxFact]
    public async Task InvalidBearerTokenIsRejected()
    {
        using var http = new HttpClient { BaseAddress = new("https://api-developer-sandbox.zocdoc.com/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", "deliberately-invalid-certification-token");
        using var response = await http.GetAsync("v1/visit_reasons?page_size=1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
