using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace WebhookReplay.Api.Features.Deliveries;

public static class GetDeliveryAttempts
{
    private const string WebhookRequestExistsSql = """
        SELECT 1 FROM webhook_requests WHERE id = @id
        """;

    private const string ListSql = """
        SELECT id, target_url, status_code, response_body, duration_ms, attempted_at,
               request_headers::text AS request_headers, request_body
        FROM delivery_attempts
        WHERE webhook_request_id = @id
        ORDER BY attempted_at DESC
        """;

    public static async Task<IResult> HandleAsync(
        string id,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var requestId))
        {
            return Results.NotFound(new { error = $"Webhook '{id}' not found." });
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        if (!await WebhookRequestExistsAsync(connection, requestId, cancellationToken))
        {
            return Results.NotFound(new { error = $"Webhook '{id}' not found." });
        }

        var items = new List<object>();

        await using var command = new NpgsqlCommand(ListSql, connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = requestId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var statusCode = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
            var responseBody = reader.IsDBNull(3) ? null : reader.GetString(3);
            var requestHeadersJson = reader.IsDBNull(6) ? null : reader.GetString(6);
            var requestBody = reader.IsDBNull(7) ? null : reader.GetString(7);
            items.Add(new
            {
                id = reader.GetGuid(0),
                targetUrl = reader.GetString(1),
                statusCode,
                responseBody,
                durationMs = reader.GetInt32(4),
                attemptedAt = reader.GetFieldValue<DateTime>(5),
                requestHeaders = requestHeadersJson is null
                    ? null
                    : (JsonElement?)JsonSerializer.Deserialize<JsonElement>(requestHeadersJson),
                requestBody
            });
        }

        return Results.Ok(new { items });
    }

    private static async Task<bool> WebhookRequestExistsAsync(
        NpgsqlConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(WebhookRequestExistsSql, connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }
}
