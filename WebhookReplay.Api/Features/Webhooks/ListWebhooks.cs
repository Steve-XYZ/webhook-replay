using Npgsql;
using NpgsqlTypes;

namespace WebhookReplay.Api.Features.Webhooks;

public static class ListWebhooks
{
    private const string EndpointExistsSql = """
        SELECT id FROM endpoints WHERE id = @id LIMIT 1
        """;

    private const string ListSql = """
        SELECT id, method, body_text, received_at
        FROM webhook_requests
        WHERE endpoint_id = @endpoint_id AND (@before IS NULL OR received_at < @before)
          AND (@q IS NULL OR body_text ILIKE '%' || @q || '%')
        ORDER BY received_at DESC
        LIMIT @limit
        """;

    public static async Task<IResult> HandleAsync(
        string endpointId,
        int? limit,
        DateTime? before,
        string? q,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(endpointId, out var endpointGuid))
        {
            return Results.NotFound(new { error = $"Endpoint '{endpointId}' not found." });
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        if (!await EndpointExistsAsync(connection, endpointGuid, cancellationToken))
        {
            return Results.NotFound(new { error = $"Endpoint '{endpointId}' not found." });
        }

        var effectiveLimit = limit ?? 50;
        if (effectiveLimit < 1)
        {
            effectiveLimit = 1;
        }
        else if (effectiveLimit > 100)
        {
            effectiveLimit = 100;
        }

        var beforeUtc = before;
        if (beforeUtc.HasValue && beforeUtc.Value.Kind != DateTimeKind.Utc)
        {
            beforeUtc = beforeUtc.Value.ToUniversalTime();
        }

        var effectiveQuery = string.IsNullOrWhiteSpace(q) ? null : EscapeLikePattern(q);

        var items = new List<object>();
        DateTime? lastReceivedAt = null;

        await using var command = new NpgsqlCommand(ListSql, connection);
        command.Parameters.Add("endpoint_id", NpgsqlDbType.Uuid).Value = endpointGuid;
        command.Parameters.Add("before", NpgsqlDbType.TimestampTz).Value =
            beforeUtc.HasValue ? beforeUtc.Value : DBNull.Value;
        command.Parameters.Add("q", NpgsqlDbType.Text).Value = effectiveQuery ?? (object)DBNull.Value;
        command.Parameters.Add("limit", NpgsqlDbType.Integer).Value = effectiveLimit;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var receivedAt = reader.GetFieldValue<DateTime>(3);
            var bodyText = reader.GetString(2);
            items.Add(new
            {
                id = reader.GetGuid(0),
                method = reader.GetString(1),
                receivedAt,
                bodyPreview = bodyText.Length <= 200 ? bodyText : bodyText[..200]
            });
            lastReceivedAt = receivedAt;
        }

        return Results.Ok(new
        {
            items,
            nextBefore = items.Count == effectiveLimit ? lastReceivedAt : null
        });
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static async Task<bool> EndpointExistsAsync(
        NpgsqlConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(EndpointExistsSql, connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid;
    }
}
