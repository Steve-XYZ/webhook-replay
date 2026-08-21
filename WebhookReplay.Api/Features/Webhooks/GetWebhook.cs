using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace WebhookReplay.Api.Features.Webhooks;

public static class GetWebhook
{
    private const string SelectRequestSql = """
        SELECT endpoint_id, method, headers::text AS headers, body_text, body_json::text AS body_json, received_at
        FROM webhook_requests
        WHERE id = @id
        """;

    private const string SelectAttemptsSql = """
        SELECT id, target_url, status_code, response_body, duration_ms, attempted_at
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

        Guid endpointId;
        string method;
        string headersJson;
        string bodyText;
        string? bodyJsonText;
        DateTime receivedAt;

        await using (var command = new NpgsqlCommand(SelectRequestSql, connection))
        {
            command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = requestId;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return Results.NotFound(new { error = $"Webhook '{id}' not found." });
            }

            endpointId = reader.GetGuid(0);
            method = reader.GetString(1);
            headersJson = reader.GetString(2);
            bodyText = reader.GetString(3);
            bodyJsonText = reader.IsDBNull(4) ? null : reader.GetString(4);
            receivedAt = reader.GetDateTime(5);
        }

        var attempts = new List<object>();

        await using (var command = new NpgsqlCommand(SelectAttemptsSql, connection))
        {
            command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = requestId;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                attempts.Add(new
                {
                    id = reader.GetGuid(0),
                    targetUrl = reader.GetString(1),
                    statusCode = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
                    responseBody = reader.IsDBNull(3) ? null : reader.GetString(3),
                    durationMs = reader.GetInt32(4),
                    attemptedAt = reader.GetDateTime(5)
                });
            }
        }

        return Results.Ok(new
        {
            id = requestId,
            endpointId,
            method,
            headers = JsonSerializer.Deserialize<JsonElement>(headersJson),
            bodyText,
            bodyJson = bodyJsonText is null ? null : (JsonElement?)JsonSerializer.Deserialize<JsonElement>(bodyJsonText),
            receivedAt,
            attempts
        });
    }
}
