using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace WebhookReplay.Api.Features.Webhooks;

public static class IngestRateLimiting
{
    private const string PolicyName = "ingest";

    public static IServiceCollection AddIngestRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var permitLimit = configuration.GetValue("IngestRateLimit:PermitLimit", 100);
        var windowSeconds = configuration.GetValue("IngestRateLimit:WindowSeconds", 60);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(PolicyName, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "global",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromSeconds(windowSeconds),
                        QueueLimit = 0,
                    }));
        });

        return services;
    }
}
