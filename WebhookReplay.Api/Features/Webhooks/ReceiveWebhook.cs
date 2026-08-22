using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace WebhookReplay.Api.Features.Webhooks;

public static class ReceiveWebhook
{
    private const string SignatureHeader = "X-Webhook-Signature";
    private const string SignaturePrefix = "sha256=";
    private const int SignatureHexLength = 64;

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

        var endpoint = await FindEndpointAsync(connection, slug, cancellationToken);
        if (endpoint is null)
        {
            return Results.NotFound(new { error = $"Unknown endpoint '{slug}'." });
        }

        var bodyBytes = await ReadBodyBytesAsync(request, cancellationToken);

        if (endpoint.Value.Secret is { } secret && !HasValidSignature(request.Headers, secret, bodyBytes))
        {
            return Results.Json(
                new { error = "Missing or invalid signature." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var bodyText = Encoding.UTF8.GetString(bodyBytes);
        var headersJson = JsonSerializer.Serialize(
            request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray()));

        await using var command = new NpgsqlCommand(InsertSql, connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = Guid.CreateVersion7();
        command.Parameters.Add("endpoint_id", NpgsqlDbType.Uuid).Value = endpoint.Value.Id;
        command.Parameters.Add("method", NpgsqlDbType.Text).Value = request.Method;
        command.Parameters.Add("headers", NpgsqlDbType.Jsonb).Value = headersJson;
        command.Parameters.Add("body_text", NpgsqlDbType.Text).Value = bodyText;
        command.Parameters.Add("body_json", NpgsqlDbType.Jsonb).Value =
            IsValidJson(bodyText) ? bodyText : DBNull.Value;
        command.Parameters.Add("received_at", NpgsqlDbType.TimestampTz).Value = DateTime.UtcNow;
        await command.ExecuteNonQueryAsync(cancellationToken);

        return Results.NoContent();
    }

    private static async Task<(Guid Id, string? Secret)?> FindEndpointAsync(
        NpgsqlConnection connection,
        string slug,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT id, secret FROM endpoints WHERE slug = @slug LIMIT 1", connection);
        command.Parameters.AddWithValue("slug", slug);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var secret = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (reader.GetGuid(0), secret);
    }

    private static bool HasValidSignature(
        IHeaderDictionary headers,
        string secret,
        byte[] bodyBytes)
    {
        if (!headers.TryGetValue(SignatureHeader, out var values))
        {
            return false;
        }

        var provided = values.ToString().Trim();
        if (provided.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
        {
            provided = provided[SignaturePrefix.Length..].Trim();
        }

        provided = provided.ToLowerInvariant();
        if (provided.Length != SignatureHexLength)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expectedHex = Convert.ToHexString(hmac.ComputeHash(bodyBytes)).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(provided),
            Encoding.ASCII.GetBytes(expectedHex));
    }

    private static async Task<byte[]> ReadBodyBytesAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
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
