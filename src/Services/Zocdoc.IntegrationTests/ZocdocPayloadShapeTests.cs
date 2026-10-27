using System.Text.Json;
using Xunit.Abstractions;

namespace Zocdoc.IntegrationTests;

/// <summary>
/// Reports the field structure (JSON paths and value kinds, never values) of the
/// sandbox payloads the adapter reads, so DTO changes such as capturing Zocdoc's
/// insurance hint can be designed from the real response shape.
/// </summary>
public sealed class ZocdocPayloadShapeTests(ITestOutputHelper output)
{
    private static readonly string[] AppointmentShapeCases =
    [
        "d2ee5bd8-643a-42c8-8c5a-be450e903430", // confirmed, new patient
        "423e6a11-8dac-4873-b933-d8d02f9a370f", // confirmed, existing patient
        "2b29f79b-6d7f-472a-9603-d0c378bc9531"  // pending_booking
    ];

    [ZocdocSandboxFact]
    public async Task ReportAppointmentPayloadShape()
    {
        using var client = new ZocdocSandboxClient();
        await client.AuthenticateAsync();
        var paths = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var id in AppointmentShapeCases)
        {
            using var appointment = await client.GetAsync($"v1/appointments/{id}");
            Collect(appointment.RootElement, "$", paths);
        }

        Write("appointment", paths);
        Assert.Contains("$.status", paths.Keys);
    }

    [ZocdocSandboxFact]
    public async Task ReportReferenceDataPayloadShape()
    {
        using var client = new ZocdocSandboxClient();
        await client.AuthenticateAsync();
        foreach (var (name, path) in new[]
                 {
                     ("schedulable_entities", "v1/schedulable_entities?page_size=5"),
                     ("visit_reasons", "v1/visit_reasons?page_size=5")
                 })
        {
            using var document = await client.GetAsync(path);
            var paths = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            Collect(document.RootElement, "$", paths);
            Write(name, paths);
            Assert.Contains("$.data", paths.Keys);
        }
    }

    private static void Collect(JsonElement element, string path,
        SortedDictionary<string, SortedSet<string>> paths)
    {
        if (!paths.TryGetValue(path, out var kinds))
            paths[path] = kinds = new SortedSet<string>(StringComparer.Ordinal);
        kinds.Add(element.ValueKind.ToString());

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Collect(property.Value, $"{path}.{property.Name}", paths);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, $"{path}[]", paths);
                break;
        }
    }

    private void Write(string payload, SortedDictionary<string, SortedSet<string>> paths)
    {
        output.WriteLine($"Zocdoc sandbox {payload} payload shape:");
        foreach (var (path, kinds) in paths)
            output.WriteLine($"  {path}: {string.Join(" | ", kinds)}");
    }
}
