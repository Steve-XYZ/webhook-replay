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
        INSERT INTO delivery_attempts (id, webhook_request_id, target_url, status_code, response_body, duration_ms, attempted_at, request_headers, request_body)
        VALUES (@id, @webhook_request_id, @target_url, @status_code, @response_body, @duration_ms, @attempted_at, @request_headers, @request_body)
        """;

    public static async Task<IResult> HandleAsync(
        string id,
        HttpRequest request,
        NpgsqlDataSource dataSource,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var requestId))
        {
            return Results.NotFound(new { error = $"Webhook '{id}' not found." });
        }

        var overrides = await ParseOverridesAsync(request, cancellationToken);
        if (overrides.Error is not null)
        {
            return Results.BadRequest(new { error = overrides.Error });
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var stored = await LoadStoredRequestAsync(connection, requestId, cancellationToken);
        if (stored is null)
        {
            return Results.NotFound(new { error = $"Webhook '{id}' not found." });
        }

        var payload = ResolveEffectivePayload(stored, overrides.Value);
        if (!Uri.TryCreate(payload.TargetUrl, UriKind.Absolute, out var targetUri) ||
            (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
        {
            return Results.BadRequest(new { error = "Target URL must be an absolute http(s) URL." });
        }

        return await SendAndRecordAsync(connection, requestId, payload, httpClientFactory, cancellationToken);
    }

    internal sealed record EffectivePayload(string Method, string TargetUrl, JsonElement Headers, string BodyText);

    private sealed record StoredRequest(string Method, JsonElement Headers, string BodyText, string ForwardUrl);

    private sealed record ReplayOverrides(string? TargetUrl, Dictionary<string, string[]>? Headers, string? Body);

    private readonly record struct OverridesParse(ReplayOverrides? Value, string? Error);

    private static async Task<OverridesParse> ParseOverridesAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0)
        {
            return new OverridesParse(null, null);
        }

        ReplayOverrides? overrides;
        try
        {
            overrides = JsonSerializer.Deserialize<ReplayOverrides>(
                buffer.ToArray(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return new OverridesParse(null, "Replay overrides must be a valid JSON object.");
        }

        if (overrides is null)
        {
            return new OverridesParse(null, null);
        }

        if (overrides.Headers is not null &&
            overrides.Headers.Any(header => header.Value is null))
        {
            return new OverridesParse(null, "Header override values must be arrays of strings.");
        }

        return new OverridesParse(overrides, null);
    }

    private static EffectivePayload ResolveEffectivePayload(StoredRequest stored, ReplayOverrides? overrides)
    {
        if (overrides is null)
        {
            return new EffectivePayload(stored.Method, stored.ForwardUrl, stored.Headers, stored.BodyText);
        }

        var headers = stored.Headers;
        if (overrides.Headers is not null)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(overrides.Headers));
            headers = document.RootElement.Clone();
        }

        return new EffectivePayload(
            stored.Method,
            overrides.TargetUrl ?? stored.ForwardUrl,
            headers,
            overrides.Body ?? stored.BodyText);
    }

    private static async Task<IResult> SendAndRecordAsync(
        NpgsqlConnection connection,
        Guid requestId,
        EffectivePayload payload,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var requestMessage = BuildOutgoingRequest(payload);
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
                connection, attemptId, requestId, payload.TargetUrl, statusCode, responseBody,
                stopwatch.ElapsedMilliseconds, attemptedAt, payload, CancellationToken.None);

            return Results.Ok(SerializeAttempt(attemptId, payload, statusCode, responseBody,
                stopwatch.ElapsedMilliseconds, attemptedAt));
        }
        catch (Exception ex) when ((ex is OperationCanceledException or HttpRequestException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return await RecordFailedAttemptAsync(
                connection, requestId, payload, stopwatch.ElapsedMilliseconds);
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

    private static HttpRequestMessage BuildOutgoingRequest(EffectivePayload payload)
    {
        var requestMessage = new HttpRequestMessage(new HttpMethod(payload.Method), new Uri(payload.TargetUrl))
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload.BodyText))
        };

        foreach (var header in payload.Headers.EnumerateObject())
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
        EffectivePayload payload,
        long durationMs)
    {
        var attemptId = Guid.CreateVersion7();
        var attemptedAt = DateTime.UtcNow;
        await InsertAttemptAsync(connection, attemptId, requestId, payload.TargetUrl, null, null,
            durationMs, attemptedAt, payload, CancellationToken.None);

        return Results.Json(
            SerializeAttempt(attemptId, payload, null, null, durationMs, attemptedAt),
            statusCode: 502);
    }

    private static object SerializeAttempt(
        Guid attemptId,
        EffectivePayload payload,
        int? statusCode,
        string? responseBody,
        long durationMs,
        DateTime attemptedAt)
    {
        return new
        {
            id = attemptId,
            targetUrl = payload.TargetUrl,
            statusCode,
            responseBody,
            durationMs = (int)durationMs,
            attemptedAt,
            requestHeaders = payload.Headers,
            requestBody = payload.BodyText
        };
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
        EffectivePayload payload,
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
        command.Parameters.Add("request_headers", NpgsqlDbType.Jsonb).Value =
            JsonSerializer.Serialize(payload.Headers);
        command.Parameters.Add("request_body", NpgsqlDbType.Text).Value = payload.BodyText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
