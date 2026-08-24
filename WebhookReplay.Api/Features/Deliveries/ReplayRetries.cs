namespace WebhookReplay.Api.Features.Deliveries;

public sealed record ReplayRetryOptions(int MaxAttempts, int BackoffBaseSeconds);

public static class ReplayRetriesServiceCollectionExtensions
{
    public static IServiceCollection AddReplayRetries(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var maxAttempts = Math.Clamp(configuration.GetValue("Retry:MaxAttempts", 1), 1, 10);
        var backoffBaseSeconds = Math.Max(0, configuration.GetValue("Retry:BackoffBaseSeconds", 1));

        return services.AddSingleton(new ReplayRetryOptions(maxAttempts, backoffBaseSeconds));
    }
}
