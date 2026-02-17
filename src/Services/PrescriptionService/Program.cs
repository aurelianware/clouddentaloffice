// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.EntityFrameworkCore;
using PrescriptionService.Adapters;
using PrescriptionService.Domain;
using PrescriptionService.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// ── Database ────────────────────────────────────────────────────────────────

var dbProvider = builder.Configuration.GetValue<string>("DatabaseProvider") ?? "SQLite";

builder.Services.AddDbContext<PrescriptionDbContext>(options =>
{
    switch (dbProvider)
    {
        case "PostgreSQL":
            options.UseNpgsql(builder.Configuration.GetConnectionString("PrescriptionDb"));
            break;
        case "SqlServer":
            options.UseSqlServer(builder.Configuration.GetConnectionString("PrescriptionDb"));
            break;
        default: // SQLite for local dev
            options.UseSqlite("Data Source=prescriptions.db");
            break;
    }
});

// ── eRx Gateway (DoseSpot / Mock) ───────────────────────────────────────────

var erxProvider = builder.Configuration.GetValue<string>("ErxProvider") ?? "Mock";

switch (erxProvider)
{
    case "DoseSpot":
        builder.Services.Configure<DoseSpotOptions>(
            builder.Configuration.GetSection(DoseSpotOptions.SectionName));

        builder.Services.AddHttpClient<IErxGateway, DoseSpotGateway>(client =>
        {
            var options = builder.Configuration.GetSection(DoseSpotOptions.SectionName).Get<DoseSpotOptions>();
            client.BaseAddress = new Uri(options?.UseSandbox == true
                ? options.SandboxBaseUrl
                : options?.ApiBaseUrl ?? "https://my.dosespot.com");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        break;

    default: // Mock for development
        builder.Services.AddSingleton<IErxGateway, MockErxGateway>();
        break;
}

// ── EPCS Auth Providers ─────────────────────────────────────────────────────

// DoseSpot built-in (always available)
builder.Services.AddSingleton<DoseSpotEpcsAuthProvider>();

// Imprivata (optional — only for enterprise deployments)
var imprivataEnabled = builder.Configuration.GetValue<bool>("Imprivata:Enabled");
if (imprivataEnabled)
{
    builder.Services.Configure<ImprivataOptions>(
        builder.Configuration.GetSection(ImprivataOptions.SectionName));

    builder.Services.AddHttpClient<ImprivataEpcsAuthProvider>(client =>
    {
        var options = builder.Configuration.GetSection(ImprivataOptions.SectionName).Get<ImprivataOptions>();
        client.BaseAddress = new Uri(options?.BaseUrl ?? "https://localhost");
        client.DefaultRequestHeaders.Add("X-Imprivata-ApiKey", options?.ApiKey ?? "");
        client.Timeout = TimeSpan.FromSeconds(15);
    });
}
else
{
    // Register a no-op so DI doesn't fail if someone references Imprivata
    builder.Services.AddSingleton<ImprivataEpcsAuthProvider>(sp =>
        new ImprivataEpcsAuthProvider(
            new HttpClient(), // unused
            Microsoft.Extensions.Options.Options.Create(new ImprivataOptions()),
            sp.GetRequiredService<ILogger<ImprivataEpcsAuthProvider>>()));
}

builder.Services.AddSingleton<EpcsAuthProviderFactory>();

// ── OpenAPI ─────────────────────────────────────────────────────────────────

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "CloudDentalOffice Prescription Service",
        Version = "v1",
        Description = "eRx microservice — DoseSpot integration, EPCS auth, FHIR MedicationRequest"
    });
});

// ── Health checks ───────────────────────────────────────────────────────────

builder.Services.AddHealthChecks()
    .AddDbContextCheck<PrescriptionDbContext>();

// ── CORS (for Portal) ───────────────────────────────────────────────────────

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

// ── Database initialization (dev only) ──────────────────────────────────────

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PrescriptionDbContext>();
    await db.Database.EnsureCreatedAsync();

    app.UseSwagger();
    app.UseSwaggerUI();
}

// ── Middleware ───────────────────────────────────────────────────────────────

app.UseCors();
app.MapHealthChecks("/health");

// ── Endpoints ───────────────────────────────────────────────────────────────

app.MapPrescriptionEndpoints();

app.Run();
