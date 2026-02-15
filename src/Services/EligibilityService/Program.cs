using CloudDentalOffice.Contracts.Eligibility;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "Eligibility Service", Version = "v1" }));
builder.Services.AddHealthChecks();

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapHealthChecks("/health");

app.MapPost("/api/eligibility/check", (EligibilityRequest request) =>
{
    // TODO: Generate 270 transaction, submit to payer/clearinghouse, parse 271 response
    var response = new EligibilityResponse
    {
        RequestId = Guid.NewGuid(),
        Status = EligibilityStatus.Active,
        PlanName = "Sample Dental PPO",
        PayerName = request.PayerId,
        CoverageEffective = new DateOnly(2024, 1, 1),
        Benefits =
        [
            new() { ServiceType = "35", BenefitType = "ActiveCoverage", CoverageLevel = "Individual", Description = "Dental Care" },
            new() { ServiceType = "35", BenefitType = "Deductible", CoverageLevel = "Individual", Amount = 50m, TimePeriod = "CalendarYear", InNetworkIndicator = "Y" },
            new() { ServiceType = "35", BenefitType = "CoInsurance", Percentage = 0.20m, Description = "Basic Services", InNetworkIndicator = "Y" },
        ],
        CheckedAt = DateTime.UtcNow
    };
    return Results.Ok(response);
}).WithTags("Eligibility");

app.MapGet("/api/eligibility/history/{patientId:guid}", (Guid patientId) =>
{
    // TODO: Return stored eligibility check history from database
    return Results.Ok(Array.Empty<EligibilityResponse>());
}).WithTags("Eligibility");

app.Run();
