using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using WebhookReplay.Api.Features.Webhooks;
using WebhookReplay.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

const long maxBodyBytes = 1024 * 1024;

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxBodyBytes;
});

builder.Services.AddOpenApi();

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await DbMigrations.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);

app.MapPost("/hooks/{slug}", ReceiveWebhook.HandleAsync);

app.Run();
