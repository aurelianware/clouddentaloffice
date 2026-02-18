// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.EntityFrameworkCore;
using VisionService.Adapters;
using VisionService.Domain;
using VisionService.Endpoints;
using VisionService.Hubs;

var builder = WebApplication.CreateBuilder(args);

// ── Database ────────────────────────────────────────────────────────────────

var dbProvider = builder.Configuration.GetValue<string>("DatabaseProvider") ?? "SQLite";

builder.Services.AddDbContext<VisionDbContext>(options =>
{
    switch (dbProvider)
    {
        case "PostgreSQL":
            options.UseNpgsql(builder.Configuration.GetConnectionString("VisionDb"));
            break;
        case "SqlServer":
            options.UseSqlServer(builder.Configuration.GetConnectionString("VisionDb"));
            break;
        default: // SQLite for local dev
            options.UseSqlite("Data Source=vision.db");
            break;
    }
});

// ── OCR Gateway (Azure AI Vision / Mock) ────────────────────────────────────

var ocrProvider = builder.Configuration.GetValue<string>("OcrProvider") ?? "Mock";

switch (ocrProvider)
{
    case "AzureAiVision":
        var azureVisionOptions = builder.Configuration
            .GetSection(AzureAiVisionOptions.SectionName)
            .Get<AzureAiVisionOptions>() ?? new AzureAiVisionOptions();

        builder.Services.AddHttpClient<IOcrGateway, AzureAiVisionOcrGateway>(client =>
        {
            client.BaseAddress = new Uri(azureVisionOptions.Endpoint);
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler());

        builder.Services.AddSingleton(azureVisionOptions);
        break;

    default: // Mock for dev
        builder.Services.AddSingleton<IOcrGateway, MockOcrGateway>();
        break;
}

// ── Context Correlation Engine ──────────────────────────────────────────────

var correlationProvider = builder.Configuration.GetValue<string>("CorrelationProvider") ?? "Mock";

var practiceHours = builder.Configuration
    .GetSection(PracticeHoursConfig.SectionName)
    .Get<PracticeHoursConfig>() ?? new PracticeHoursConfig();

builder.Services.AddSingleton(practiceHours);

switch (correlationProvider)
{
    case "Live":
        // HttpClient pointing at CDO API Gateway for cross-service correlation
        builder.Services.AddHttpClient<IContextCorrelationEngine, ContextCorrelationEngine>(client =>
        {
            var gatewayUrl = builder.Configuration.GetValue<string>("ApiGatewayUrl") ?? "http://localhost:5200";
            client.BaseAddress = new Uri(gatewayUrl);
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        break;

    default: // Mock
        builder.Services.AddSingleton<IContextCorrelationEngine, MockContextCorrelationEngine>();
        break;
}

// ── SignalR ─────────────────────────────────────────────────────────────────

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaximumReceiveMessageSize = 512 * 1024; // 512KB for image payloads
});

// ── Swagger ─────────────────────────────────────────────────────────────────

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Cloud Dental Office — VisionService",
        Version = "v1",
        Description = "AI vision integration: privaseeAI detection ingestion, insurance card OCR, " +
                      "consent recording, narcotics cabinet monitoring, and clinical note assistance."
    });
});

// ── Health Checks ───────────────────────────────────────────────────────────

builder.Services.AddHealthChecks()
    .AddDbContextCheck<VisionDbContext>();

// ── CORS ────────────────────────────────────────────────────────────────────

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                "http://localhost:5000",   // Portal
                "http://localhost:5200",   // API Gateway
                "https://localhost:5001"   // Portal HTTPS
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials(); // Required for SignalR
    });
});

var app = builder.Build();

// ── Middleware Pipeline ─────────────────────────────────────────────────────

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

// ── Map Endpoints ───────────────────────────────────────────────────────────

app.MapVisionEndpoints();
app.MapHub<VisionHub>("/hubs/vision");
app.MapHealthChecks("/health");

// ── Database Init (dev only) ────────────────────────────────────────────────

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<VisionDbContext>();
    await db.Database.EnsureCreatedAsync();
    // TODO: Replace with EF Core migrations for production
}

app.Run();
