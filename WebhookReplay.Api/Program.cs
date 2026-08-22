using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using WebhookReplay.Api.Features.Deliveries;
using WebhookReplay.Api.Features.Endpoints;
using WebhookReplay.Api.Features.Health;
using WebhookReplay.Api.Features.Webhooks;
using WebhookReplay.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

const long maxBodyBytes = 1024 * 1024;

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxBodyBytes;
});

builder.Services.AddOpenApi();

builder.Services.AddIngestRateLimiting(builder.Configuration);
builder.Services.AddWebhookHealthChecks();

builder.Services.AddHttpClient();

builder.Services.AddWebhookRetention(builder.Configuration);

builder.Services.AddSingleton<NpgsqlDataSource>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    return new NpgsqlDataSourceBuilder(configuration.GetConnectionString("Default")).Build();
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();
        if (builder.Environment.IsDevelopment())
        {
            tracing.AddConsoleExporter();
        }
    });

var app = builder.Build();

app.MapOpenApi();

await DbMigrations.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);

app.UseRateLimiter();

app.MapPost("/hooks/{slug}", ReceiveWebhook.HandleAsync).RequireRateLimiting("ingest");
app.MapPost("/api/endpoints", CreateEndpoint.HandleAsync);
app.MapGet("/api/endpoints", ListEndpoints.HandleAsync);
app.MapGet("/api/endpoints/{id}", GetEndpoint.HandleAsync);
app.MapGet("/api/endpoints/{endpointId}/webhooks", ListWebhooks.HandleAsync);
app.MapGet("/api/endpoints/{endpointId}/events", StreamWebhookEvents.HandleAsync);
app.MapGet("/api/webhooks/{id}", GetWebhook.HandleAsync);
app.MapPost("/api/webhooks/{id}/replay", ReplayWebhook.HandleAsync);
app.MapGet("/api/webhooks/{id}/attempts", GetDeliveryAttempts.HandleAsync);
app.MapWebhookHealthEndpoints();

app.Run();

public partial class Program { }
