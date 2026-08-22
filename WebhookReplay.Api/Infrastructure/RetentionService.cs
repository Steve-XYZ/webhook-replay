using Npgsql;
using NpgsqlTypes;

namespace WebhookReplay.Api.Infrastructure;

public sealed class RetentionService(
    NpgsqlDataSource dataSource,
    ILogger<RetentionService> logger,
    int days,
    int runIntervalMinutes) : BackgroundService
{
    private const int BatchSize = 1000;

    private const string SelectExpiredIdsSql = """
        SELECT id FROM webhook_requests WHERE received_at < @cutoff LIMIT @batch
        """;

    private const string DeleteAttemptsSql = """
        DELETE FROM delivery_attempts WHERE webhook_request_id = ANY(@ids)
        """;

    private const string DeleteRequestsSql = """
        DELETE FROM webhook_requests WHERE id = ANY(@ids)
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var effectiveDays = Math.Max(0, days);
        var effectiveInterval = TimeSpan.FromMinutes(Math.Max(1, runIntervalMinutes));

        logger.LogInformation(
            "Retention policy enabled: deleting webhook_requests older than {Days} days every {IntervalMinutes} minutes",
            effectiveDays,
            effectiveInterval.TotalMinutes);

        using var timer = new PeriodicTimer(effectiveInterval);

        try
        {
            do
            {
                try
                {
                    await RunPassAsync(effectiveDays, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Retention pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunPassAsync(int effectiveDays, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.AddDays(-effectiveDays);
        var batches = 0;
        long totalRequestsDeleted = 0;
        long totalAttemptsDeleted = 0;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ids = await SelectExpiredIdsAsync(connection, cutoff, cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var attemptsDeleted = await DeleteAsync(
                connection, transaction, DeleteAttemptsSql, ids, cancellationToken);
            var requestsDeleted = await DeleteAsync(
                connection, transaction, DeleteRequestsSql, ids, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            batches++;
            totalRequestsDeleted += requestsDeleted;
            totalAttemptsDeleted += attemptsDeleted;
        }

        logger.LogInformation(
            "Retention pass complete: {Batches} batch(es), {RequestsDeleted} webhook_request(s) and {AttemptsDeleted} delivery_attempt(s) deleted (received_at < {Cutoff})",
            batches,
            totalRequestsDeleted,
            totalAttemptsDeleted,
            cutoff);
    }

    private async Task<List<Guid>> SelectExpiredIdsAsync(
        NpgsqlConnection connection,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        var ids = new List<Guid>(BatchSize);

        await using var command = new NpgsqlCommand(SelectExpiredIdsSql, connection);
        command.Parameters.Add("cutoff", NpgsqlDbType.TimestampTz).Value = cutoff;
        command.Parameters.Add("batch", NpgsqlDbType.Integer).Value = BatchSize;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<long> DeleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        List<Guid> ids,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = ids.ToArray();

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public static class RetentionServiceCollectionExtensions
{
    public static IServiceCollection AddWebhookRetention(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (!configuration.GetValue("Retention:Enabled", false))
        {
            return services;
        }

        return services.AddHostedService(sp => new RetentionService(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<ILogger<RetentionService>>(),
            configuration.GetValue("Retention:Days", 30),
            configuration.GetValue("Retention:RunIntervalMinutes", 60)));
    }
}
