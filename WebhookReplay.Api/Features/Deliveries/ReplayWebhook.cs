using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace WebhookReplay.Api.Features.Deliveries;

public static class ReplayWebhook
{
    private const int MaxResponseBytes = 65537;
    private const int MaxResponseChars = 65536;

    private const string SelectSql = """
        SELECT wr.method, wr.headers::text, wr.body_text, e.forward_url
        FROM webhook_requests wr
        JOIN endpoints e ON e.id = wr.endpoint_id
        WHERE wr.id = @id
        """;

    private const string InsertSql = """
        INSERT INTO delivery_attempts (id, webhook_request_id, target_url, status_code, response_body, duration_ms, attempted_at)
        VALUES (@id, @webhook_request_id, @target_url, @status_code, @response_body, @duration_ms, @attempted_at)
        """;

    public static async Task<IResult> HandleAsync(
        string id,
        NpgsqlDataSource dataSource,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var requestId))
        {
            return Results.NotFound(new { error = $"Webhook '{id}' not found." });
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var stored = await LoadStoredRequestAsync(connection, requestId, cancellationToken);
        if (stored is null)
        {
            return Results.NotFound(new { error = $"Webhook '{id}' not found." });
        }

        using var requestMessage = BuildOutgoingRequest(stored);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var client = httpClientFactory.CreateClient("replay");
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(30));

            using var response = await client.SendAsync(
                requestMessage, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            var statusCode = (int)response.StatusCode;

            using var responseStream = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
            var responseBody = await ReadBoundedBodyAsync(responseStream, timeoutSource.Token);

            stopwatch.Stop();
            var attemptId = Guid.CreateVersion7();
            var attemptedAt = DateTime.UtcNow;
            await InsertAttemptAsync(
                connection, attemptId, requestId, stored.ForwardUrl, statusCode, responseBody,
                stopwatch.ElapsedMilliseconds, attemptedAt, CancellationToken.None);

            return Results.Ok(new
            {
                id = attemptId,
                targetUrl = stored.ForwardUrl,
                statusCode,
                responseBody,
                durationMs = (int)stopwatch.ElapsedMilliseconds,
                attemptedAt
            });
        }
        catch (Exception ex) when ((ex is OperationCanceledException or HttpRequestException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return await RecordFailedAttemptAsync(
                connection, requestId, stored.ForwardUrl, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            stopwatch.Stop();
        }
    }

    private static async Task<StoredRequest?> LoadStoredRequestAsync(
        NpgsqlConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SelectSql, connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredRequest(
            reader.GetString(0),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3));
    }

    private static HttpRequestMessage BuildOutgoingRequest(StoredRequest stored)
    {
        var requestMessage = new HttpRequestMessage(new HttpMethod(stored.Method), new Uri(stored.ForwardUrl))
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(stored.BodyText))
        };

        foreach (var header in stored.Headers.EnumerateObject())
        {
            if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = header.Value.Deserialize<string[]>();
            if (values is null || values.Length == 0)
            {
                continue;
            }

            if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                requestMessage.Content.Headers.TryAddWithoutValidation("Content-Type", values);
                continue;
            }

            requestMessage.Headers.TryAddWithoutValidation(header.Name, values);
        }

        return requestMessage;
    }

    private static async Task<string> ReadBoundedBodyAsync(Stream responseStream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var totalBytes = 0;
        int bytesRead;
        while (totalBytes < MaxResponseBytes &&
               (bytesRead = await responseStream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            var toCopy = Math.Min(bytesRead, MaxResponseBytes - totalBytes);
            await buffer.WriteAsync(chunk.AsMemory(0, toCopy), cancellationToken);
            totalBytes += toCopy;
        }

        var responseBody = Encoding.UTF8.GetString(buffer.ToArray());
        return responseBody.Length > MaxResponseChars ? responseBody[..MaxResponseChars] : responseBody;
    }

    private static async Task<IResult> RecordFailedAttemptAsync(
        NpgsqlConnection connection,
        Guid requestId,
        string targetUrl,
        long durationMs)
    {
        var attemptId = Guid.CreateVersion7();
        var attemptedAt = DateTime.UtcNow;
        await InsertAttemptAsync(connection, attemptId, requestId, targetUrl, null, null,
            durationMs, attemptedAt, CancellationToken.None);

        return Results.Json(new
        {
            id = attemptId,
            targetUrl,
            statusCode = (int?)null,
            responseBody = (string?)null,
            durationMs = (int)durationMs,
            attemptedAt
        }, statusCode: 502);
    }

    private static async Task InsertAttemptAsync(
        NpgsqlConnection connection,
        Guid attemptId,
        Guid requestId,
        string targetUrl,
        int? statusCode,
        string? responseBody,
        long durationMs,
        DateTime attemptedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(InsertSql, connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = attemptId;
        command.Parameters.Add("webhook_request_id", NpgsqlDbType.Uuid).Value = requestId;
        command.Parameters.Add("target_url", NpgsqlDbType.Text).Value = targetUrl;
        command.Parameters.Add("status_code", NpgsqlDbType.Integer).Value =
            statusCode.HasValue ? statusCode.Value : DBNull.Value;
        command.Parameters.Add("response_body", NpgsqlDbType.Text).Value =
            responseBody is null ? DBNull.Value : responseBody;
        command.Parameters.Add("duration_ms", NpgsqlDbType.Integer).Value = (int)durationMs;
        command.Parameters.Add("attempted_at", NpgsqlDbType.TimestampTz).Value = attemptedAt;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record StoredRequest(string Method, JsonElement Headers, string BodyText, string ForwardUrl);
}
