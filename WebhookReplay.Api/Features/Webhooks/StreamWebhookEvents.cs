using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace WebhookReplay.Api.Features.Webhooks;

public static class StreamWebhookEvents
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private const string EndpointExistsSql = """
        SELECT id FROM endpoints WHERE id = @id LIMIT 1
        """;

    public static async Task HandleAsync(
        string endpointId,
        HttpContext httpContext,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(endpointId, out var endpointGuid))
        {
            await NotFoundAsync(httpContext, endpointId);
            return;
        }

        bool endpointExists;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            endpointExists = await EndpointExistsAsync(connection, endpointGuid, cancellationToken);
        }
        if (!endpointExists)
        {
            await NotFoundAsync(httpContext, endpointId);
            return;
        }

        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        var subscription = LiveFeed.Subscribe(endpointGuid);
        try
        {
            await response.WriteAsync("retry: 5000\n\n: connected\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var readTask = subscription.Channel.Reader.WaitToReadAsync(linked.Token).AsTask();
                var heartbeatTask = Task.Delay(HeartbeatInterval, linked.Token);
                var completed = await Task.WhenAny(readTask, heartbeatTask);

                if (completed == heartbeatTask)
                {
                    if (!heartbeatTask.IsCompletedSuccessfully)
                    {
                        break;
                    }
                    await response.WriteAsync(
                        $": heartbeat {DateTime.UtcNow:O}\n\n", cancellationToken);
                    continue;
                }

                if (!readTask.IsCompletedSuccessfully || !readTask.Result)
                {
                    break;
                }

                var wrote = false;
                while (subscription.Channel.Reader.TryRead(out var frame))
                {
                    await response.WriteAsync(frame, cancellationToken);
                    wrote = true;
                }
                if (wrote)
                {
                    await response.Body.FlushAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            LiveFeed.Unsubscribe(subscription);
        }
    }

    private static async Task NotFoundAsync(HttpContext httpContext, string endpointId)
    {
        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        await httpContext.Response.WriteAsJsonAsync(
            new { error = $"Endpoint '{endpointId}' not found." }, httpContext.RequestAborted);
    }

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
