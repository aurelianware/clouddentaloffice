using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

public sealed class PublicBookingTests
{
    [Fact]
    public void CredentialResolvesItsTenant()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PublicBooking:Clients:0:ApiKey"] = "practice-secret",
            ["PublicBooking:Clients:0:TenantId"] = "practice-a"
        }).Build();
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer practice-secret";
        Assert.Equal("practice-a", IntakeAuth.ResolveTenant(http, config.GetSection("PublicBooking")));
    }

    [Fact]
    public void InvalidCredentialIsRejected()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["PublicBooking:ApiKey"] = "expected", ["PublicBooking:TenantId"] = "practice-a" }).Build();
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Api-Key"] = "wrong";
        Assert.Null(IntakeAuth.ResolveTenant(http, config.GetSection("PublicBooking")));
    }

    [Fact]
    public void ValidationRejectsMissingFieldsAndTimezone()
    {
        var request = new PublicBookingRequest { PreferredStart = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(1), DateTimeKind.Unspecified) };
        var errors = PublicBookingValidator.Validate(request, DateTime.UtcNow);
        Assert.Contains("name", errors.Keys);
        Assert.Contains("phone", errors.Keys);
        Assert.Contains("preferredStart", errors.Keys);
    }

    [Fact]
    public void ValidFutureRequestPassesValidation()
    {
        var request = new PublicBookingRequest { Name = "Sam", Phone = "4805550100", PreferredStart = DateTime.UtcNow.AddDays(1), PatientRelationship = PatientRelationship.New };
        Assert.Empty(PublicBookingValidator.Validate(request, DateTime.UtcNow));
    }

    [Fact]
    public void ValidationAcceptsEmailWithSurroundingWhitespace()
    {
        var request = new PublicBookingRequest
        {
            Name = "Sam",
            Phone = "4805550100",
            Email = "  sam@example.com  ",
            PreferredStart = DateTime.UtcNow.AddDays(1),
            PatientRelationship = PatientRelationship.New
        };

        Assert.Empty(PublicBookingValidator.Validate(request, DateTime.UtcNow));
    }

    [Fact]
    public void ValidationRejectsOversizedInvalidAndExcessivelyDistantData()
    {
        var request = new PublicBookingRequest
        {
            Name = new string('x', 201), Phone = "12", Email = "not-an-email",
            PreferredStart = DateTime.UtcNow.AddYears(2), DurationMinutes = 300,
            Reason = new string('r', 501), Message = new string('m', 2001)
        };
        var errors = PublicBookingValidator.Validate(request, DateTime.UtcNow);
        Assert.All(new[] { "name", "phone", "email", "preferredStart", "durationMinutes", "reason", "message", "patientRelationship" },
            key => Assert.Contains(key, errors.Keys));
    }

    [Fact]
    public void IdempotencyIsStableWithinTenantAndScopedAcrossTenants()
    {
        var first = Idempotency.CreateEventId("third-set-smiles", "request-12345");
        Assert.Equal(first, Idempotency.CreateEventId("third-set-smiles", "request-12345"));
        Assert.NotEqual(first, Idempotency.CreateEventId("another-practice", "request-12345"));
    }

    [Fact]
    public void CredentialCannotResolveAnotherClientsTenant()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PublicBooking:Clients:0:ApiKey"] = "first-practice-secret",
            ["PublicBooking:Clients:0:TenantId"] = "third-set-smiles",
            ["PublicBooking:Clients:1:ApiKey"] = "second-practice-secret",
            ["PublicBooking:Clients:1:TenantId"] = "other"
        }).Build();
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer first-practice-secret";
        Assert.Equal("third-set-smiles", IntakeAuth.ResolveTenant(http, config.GetSection("PublicBooking")));
    }
}
