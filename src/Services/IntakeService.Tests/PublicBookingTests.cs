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
        var request = new PublicBookingRequest { Name = "Sam", Phone = "555", PreferredStart = DateTime.UtcNow.AddDays(1), PatientRelationship = PatientRelationship.New };
        Assert.Empty(PublicBookingValidator.Validate(request, DateTime.UtcNow));
    }
}
