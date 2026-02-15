var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddHealthChecks();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Portal", policy =>
        policy.WithOrigins("https://localhost:5000", "http://localhost:5000")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials());
});

var app = builder.Build();

app.UseCors("Portal");
app.MapHealthChecks("/health");
app.MapReverseProxy();

app.Run();
