using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace WebhookReplay.Api.Features.Health;

public sealed class DbHealthCheck(NpgsqlDataSource dataSource, ILogger<DbHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Database health check failed.");
            return HealthCheckResult.Unhealthy("Database unavailable");
        }
    }
}

public static class WebhookHealthChecks
{
    private const string DbCheckName = "db";

    public static IServiceCollection AddWebhookHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<DbHealthCheck>(DbCheckName);
        return services;
    }

    public static void MapWebhookHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = check => check.Name == DbCheckName });
    }
}
