using CloudDentalOffice.Contracts.Era;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "ERA Service", Version = "v1" }));
builder.Services.AddHealthChecks();

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapHealthChecks("/health");

app.MapPost("/api/era/upload", (HttpRequest request) =>
{
    // TODO: Accept 835 file upload, parse via EdiCommon, store, and begin matching
    return Results.Ok(new { Message = "ERA file received", Status = EraProcessingStatus.Received });
}).WithTags("ERA");

app.MapGet("/api/era/files", () =>
{
    // TODO: List received 835 files with processing status
    return Results.Ok(Array.Empty<EraFileDto>());
}).WithTags("ERA");

app.MapGet("/api/era/files/{id:guid}", (Guid id) =>
{
    // TODO: Get ERA file detail with matched/unmatched claims
    return Results.NotFound();
}).WithTags("ERA");

app.MapPost("/api/era/files/{id:guid}/post", (Guid id) =>
{
    // TODO: Auto-post matched ERA payments to claims
    return Results.Ok(new { Message = "Posting initiated", Status = EraProcessingStatus.Posted });
}).WithTags("ERA");

app.Run();
