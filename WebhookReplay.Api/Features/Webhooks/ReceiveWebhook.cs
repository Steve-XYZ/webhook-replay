using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace WebhookReplay.Api.Features.Webhooks;

public static class ReceiveWebhook
{
    private const string InsertSql = """
        INSERT INTO webhook_requests (id, endpoint_id, method, headers, body_text, body_json, received_at)
        VALUES (@id, @endpoint_id, @method, @headers, @body_text, @body_json, @received_at)
        """;

    public static async Task<IResult> HandleAsync(
        string slug,
        HttpRequest request,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var endpointId = await FindEndpointIdAsync(connection, slug, cancellationToken);
        if (endpointId is null)
        {
            return Results.NotFound(new { error = $"Unknown endpoint '{slug}'." });
        }

        var bodyText = await ReadBodyAsync(request, cancellationToken);
        var headersJson = JsonSerializer.Serialize(
            request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray()));
        var receivedAt = DateTime.UtcNow;
        var webhookId = Guid.CreateVersion7();

        await using var command = new NpgsqlCommand(InsertSql, connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = webhookId;
        command.Parameters.Add("endpoint_id", NpgsqlDbType.Uuid).Value = endpointId.Value;
        command.Parameters.Add("method", NpgsqlDbType.Text).Value = request.Method;
        command.Parameters.Add("headers", NpgsqlDbType.Jsonb).Value = headersJson;
        command.Parameters.Add("body_text", NpgsqlDbType.Text).Value = bodyText;
        command.Parameters.Add("body_json", NpgsqlDbType.Jsonb).Value =
            IsValidJson(bodyText) ? bodyText : DBNull.Value;
        command.Parameters.Add("received_at", NpgsqlDbType.TimestampTz).Value = receivedAt;
        await command.ExecuteNonQueryAsync(cancellationToken);

        LiveFeed.Publish(endpointId.Value, BuildEventFrame(webhookId, request.Method, receivedAt, bodyText));

        return Results.NoContent();
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static string BuildEventFrame(Guid id, string method, DateTime receivedAt, string bodyText)
    {
        var preview = bodyText.Length <= 200 ? bodyText : bodyText[..200];
        var data = JsonSerializer.Serialize(new { id, method, receivedAt, bodyPreview = preview }, WebJson);
        return $"event: webhook\nid: {id}\ndata: {data}\n\n";
    }

    private static async Task<Guid?> FindEndpointIdAsync(
        NpgsqlConnection connection,
        string slug,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT id FROM endpoints WHERE slug = @slug LIMIT 1", connection);
        command.Parameters.AddWithValue("slug", slug);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid id ? id : null;
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool IsValidJson(string bodyText)
    {
        try
        {
            using var document = JsonDocument.Parse(bodyText);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
