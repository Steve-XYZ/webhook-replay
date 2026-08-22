using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace WebhookReplay.Api.Tests;

[Collection(nameof(ApiCollection))]
public sealed class WebhookReplayApiTests
{
    private readonly ApiFixture _fixture;

    public WebhookReplayApiTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ReceiveWebhook_persists_request_and_list_returns_preview()
    {
        var slug = NewSlug();
        var endpointId = await _fixture.SeedEndpointAsync(slug, "http://127.0.0.1:1/unused");

        var client = _fixture.CreateClient();
        using var post = new StringContent("""{"orderId":123,"status":"paid"}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"/hooks/{slug}", post);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var list = await client.GetAsync($"/api/endpoints/{endpointId}/webhooks");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        using var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Contains("orderId", items[0].GetProperty("bodyPreview").GetString());
    }

    [Fact]
    public async Task ReceiveWebhook_unknown_slug_returns_404()
    {
        var client = _fixture.CreateClient();
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"/hooks/{NewSlug()}", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReceiveWebhook_non_json_body_is_stored_without_bodyJson()
    {
        var slug = NewSlug();
        await _fixture.SeedEndpointAsync(slug, "http://127.0.0.1:1/unused");

        var client = _fixture.CreateClient();
        using var post = new StringContent("esto no es json", Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync($"/hooks/{slug}", post);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var webhookId = await GetSingleWebhookIdAsync(client, slug);

        using var detail = await client.GetAsync($"/api/webhooks/{webhookId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        using var document = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("bodyJson").ValueKind);
        Assert.Equal("esto no es json", document.RootElement.GetProperty("bodyText").GetString());
    }

    [Fact]
    public async Task Replay_forwarded_successfully_records_attempt()
    {
        var (port, listener) = StartStubListener();
        try
        {
            var received = RespondWithNoContentOnce(listener);

            var slug = NewSlug();
            await _fixture.SeedEndpointAsync(slug, $"http://127.0.0.1:{port}/target");

            var client = _fixture.CreateClient();
            using var post = new StringContent("""{"orderId":456}""", Encoding.UTF8, "application/json");
            using var ingest = await client.PostAsync($"/hooks/{slug}", post);
            Assert.Equal(HttpStatusCode.NoContent, ingest.StatusCode);

            var webhookId = await GetSingleWebhookIdAsync(client, slug);

            using var replay = await client.PostAsync($"/api/webhooks/{webhookId}/replay", null);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

            using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(204, replayDocument.RootElement.GetProperty("statusCode").GetInt32());

            using var attempts = await client.GetAsync($"/api/webhooks/{webhookId}/attempts");
            Assert.Equal(HttpStatusCode.OK, attempts.StatusCode);

            using var attemptsDocument = JsonDocument.Parse(await attempts.Content.ReadAsStringAsync());
            var attemptItems = attemptsDocument.RootElement.GetProperty("items");
            Assert.Equal(1, attemptItems.GetArrayLength());
            Assert.Equal(204, attemptItems[0].GetProperty("statusCode").GetInt32());

            Assert.True(received.IsCompleted);
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    [Fact]
    public async Task Replay_to_unreachable_target_returns_502_and_records_null_status_attempt()
    {
        var slug = NewSlug();
        await _fixture.SeedEndpointAsync(slug, "http://127.0.0.1:59999/x");

        var client = _fixture.CreateClient();
        using var post = new StringContent("""{"orderId":789}""", Encoding.UTF8, "application/json");
        using var ingest = await client.PostAsync($"/hooks/{slug}", post);
        Assert.Equal(HttpStatusCode.NoContent, ingest.StatusCode);

        var webhookId = await GetSingleWebhookIdAsync(client, slug);

        using var replay = await client.PostAsync($"/api/webhooks/{webhookId}/replay", null);
        Assert.Equal(HttpStatusCode.BadGateway, replay.StatusCode);

        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, replayDocument.RootElement.GetProperty("statusCode").ValueKind);

        using var attempts = await client.GetAsync($"/api/webhooks/{webhookId}/attempts");
        Assert.Equal(HttpStatusCode.OK, attempts.StatusCode);

        using var attemptsDocument = JsonDocument.Parse(await attempts.Content.ReadAsStringAsync());
        var attemptItems = attemptsDocument.RootElement.GetProperty("items");
        Assert.Equal(1, attemptItems.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, attemptItems[0].GetProperty("statusCode").ValueKind);
    }

    [Fact]
    public async Task Replay_with_body_and_targetUrl_overrides_sends_new_body_to_new_url_and_snapshots_it()
    {
        var (port, listener) = StartStubListener();
        try
        {
            var captured = CaptureOneRequestOnce(listener);

            var slug = NewSlug();
            await _fixture.SeedEndpointAsync(slug, "http://127.0.0.1:1/unused");

            var client = _fixture.CreateClient();
            using var post = new StringContent("""{"orderId":456}""", Encoding.UTF8, "application/json");
            using var ingest = await client.PostAsync($"/hooks/{slug}", post);
            Assert.Equal(HttpStatusCode.NoContent, ingest.StatusCode);

            var webhookId = await GetSingleWebhookIdAsync(client, slug);

            using var overrides = new StringContent(
                $$"""{"targetUrl":"http://127.0.0.1:{{port}}/override-target","body":"new-body"}""",
                Encoding.UTF8, "application/json");
            using var replay = await client.PostAsync($"/api/webhooks/{webhookId}/replay", overrides);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

            var delivery = await captured.Task;
            Assert.EndsWith("/override-target", delivery.Path);
            Assert.Equal("new-body", delivery.Body);

            using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            var root = replayDocument.RootElement;
            Assert.Equal(204, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("new-body", root.GetProperty("requestBody").GetString());
            Assert.True(root.GetProperty("requestHeaders")
                .TryGetProperty("Content-Type", out var contentType));
            Assert.Contains("application/json", contentType[0].GetString());

            using var attempts = await client.GetAsync($"/api/webhooks/{webhookId}/attempts");
            Assert.Equal(HttpStatusCode.OK, attempts.StatusCode);
            using var attemptsDocument = JsonDocument.Parse(await attempts.Content.ReadAsStringAsync());
            Assert.Equal(1, attemptsDocument.RootElement.GetProperty("items").GetArrayLength());
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    [Fact]
    public async Task Replay_without_body_keeps_stored_payload_and_snapshots_effective_values()
    {
        var (port, listener) = StartStubListener();
        try
        {
            var captured = CaptureOneRequestOnce(listener);

            var slug = NewSlug();
            await _fixture.SeedEndpointAsync(slug, $"http://127.0.0.1:{port}/target");

            var client = _fixture.CreateClient();
            using var post = new StringContent("""{"orderId":456}""", Encoding.UTF8, "application/json");
            using var ingest = await client.PostAsync($"/hooks/{slug}", post);
            Assert.Equal(HttpStatusCode.NoContent, ingest.StatusCode);

            var webhookId = await GetSingleWebhookIdAsync(client, slug);

            using var replay = await client.PostAsync($"/api/webhooks/{webhookId}/replay", null);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

            var delivery = await captured.Task;
            Assert.EndsWith("/target", delivery.Path);
            Assert.Equal("""{"orderId":456}""", delivery.Body);

            using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            var root = replayDocument.RootElement;
            Assert.Equal(204, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("""{"orderId":456}""", root.GetProperty("requestBody").GetString());
            Assert.True(root.GetProperty("requestHeaders")
                .TryGetProperty("Content-Type", out var contentType));
            Assert.Contains("application/json", contentType[0].GetString());
            Assert.EndsWith("/target", root.GetProperty("targetUrl").GetString());
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    [Fact]
    public async Task Replay_with_invalid_targetUrl_override_returns_400_and_records_nothing()
    {
        var slug = NewSlug();
        await _fixture.SeedEndpointAsync(slug, "http://127.0.0.1:1/unused");

        var client = _fixture.CreateClient();
        using var post = new StringContent("""{"orderId":789}""", Encoding.UTF8, "application/json");
        using var ingest = await client.PostAsync($"/hooks/{slug}", post);
        Assert.Equal(HttpStatusCode.NoContent, ingest.StatusCode);

        var webhookId = await GetSingleWebhookIdAsync(client, slug);

        using var overrides = new StringContent(
            """{"targetUrl":"not-an-absolute-url"}""",
            Encoding.UTF8, "application/json");
        using var replay = await client.PostAsync($"/api/webhooks/{webhookId}/replay", overrides);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Contains("absolute", replayDocument.RootElement.GetProperty("error").GetString());

        using var attempts = await client.GetAsync($"/api/webhooks/{webhookId}/attempts");
        Assert.Equal(HttpStatusCode.OK, attempts.StatusCode);
        using var attemptsDocument = JsonDocument.Parse(await attempts.Content.ReadAsStringAsync());
        Assert.Equal(0, attemptsDocument.RootElement.GetProperty("items").GetArrayLength());
    }

    private static string NewSlug() => $"it-{Guid.NewGuid():N}";

    [Fact]
    public async Task Replay_with_MaxAttempts_three_retries_on_500_and_final_response_reflects_last_attempt()
    {
        await using var retryFactory = new RetryingApiFactory(_fixture.ConnectionString, maxAttempts: 3, backoffBaseSeconds: 1);
        var client = retryFactory.CreateClient();

        var (port, listener) = StartStubListener();
        try
        {
            var requestsServed = RespondWithStatuses(listener, 500);

            var slug = NewSlug();
            await _fixture.SeedEndpointAsync(slug, $"http://127.0.0.1:{port}/target");

            using var post = new StringContent("""{"orderId":456}""", Encoding.UTF8, "application/json");
            using var ingest = await client.PostAsync($"/hooks/{slug}", post);
            Assert.Equal(HttpStatusCode.NoContent, ingest.StatusCode);

            var webhookId = await GetSingleWebhookIdAsync(client, slug);

            var stopwatch = Stopwatch.StartNew();
            using var replay = await client.PostAsync($"/api/webhooks/{webhookId}/replay", null);
            stopwatch.Stop();
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

            using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(500, replayDocument.RootElement.GetProperty("statusCode").GetInt32());

            Assert.Equal(3, requestsServed());

            using var attempts = await client.GetAsync($"/api/webhooks/{webhookId}/attempts");
            attempts.EnsureSuccessStatusCode();

            using var attemptsDocument = JsonDocument.Parse(await attempts.Content.ReadAsStringAsync());
            var attemptItems = attemptsDocument.RootElement.GetProperty("items");
            Assert.Equal(3, attemptItems.GetArrayLength());
            foreach (var attempt in attemptItems.EnumerateArray())
            {
                Assert.Equal(500, attempt.GetProperty("statusCode").GetInt32());
            }

            Assert.True(stopwatch.Elapsed.TotalSeconds >= 2.5);
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    [Fact]
    public async Task Replay_stops_retrying_after_first_success_response()
    {
        await using var retryFactory = new RetryingApiFactory(_fixture.ConnectionString, maxAttempts: 5, backoffBaseSeconds: 0);
        var client = retryFactory.CreateClient();

        var (port, listener) = StartStubListener();
        try
        {
            var requestsServed = RespondWithStatuses(listener, 500, 204);

            var slug = NewSlug();
            await _fixture.SeedEndpointAsync(slug, $"http://127.0.0.1:{port}/target");

            using var post = new StringContent("""{"orderId":456}""", Encoding.UTF8, "application/json");
            using var ingest = await client.PostAsync($"/hooks/{slug}", post);
            Assert.Equal(HttpStatusCode.NoContent, ingest.StatusCode);

            var webhookId = await GetSingleWebhookIdAsync(client, slug);

            using var replay = await client.PostAsync($"/api/webhooks/{webhookId}/replay", null);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

            using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(204, replayDocument.RootElement.GetProperty("statusCode").GetInt32());

            await Task.Delay(200);
            Assert.Equal(2, requestsServed());

            using var attempts = await client.GetAsync($"/api/webhooks/{webhookId}/attempts");
            attempts.EnsureSuccessStatusCode();

            using var attemptsDocument = JsonDocument.Parse(await attempts.Content.ReadAsStringAsync());
            var attemptItems = attemptsDocument.RootElement.GetProperty("items");
            Assert.Equal(2, attemptItems.GetArrayLength());
            Assert.Equal(1, attemptItems.EnumerateArray().Count(a => a.GetProperty("statusCode").GetInt32() == 500));
            Assert.Equal(1, attemptItems.EnumerateArray().Count(a => a.GetProperty("statusCode").GetInt32() == 204));
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    [Fact]
    public async Task Replay_with_default_config_makes_single_attempt_even_on_500()
    {
        var client = _fixture.CreateClient();

        var (port, listener) = StartStubListener();
        try
        {
            var requestsServed = RespondWithStatuses(listener, 500);

            var slug = NewSlug();
            await _fixture.SeedEndpointAsync(slug, $"http://127.0.0.1:{port}/target");

            using var post = new StringContent("""{"orderId":456}""", Encoding.UTF8, "application/json");
            using var ingest = await client.PostAsync($"/hooks/{slug}", post);
            Assert.Equal(HttpStatusCode.NoContent, ingest.StatusCode);

            var webhookId = await GetSingleWebhookIdAsync(client, slug);

            using var replay = await client.PostAsync($"/api/webhooks/{webhookId}/replay", null);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

            using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(500, replayDocument.RootElement.GetProperty("statusCode").GetInt32());

            Assert.Equal(1, requestsServed());

            using var attempts = await client.GetAsync($"/api/webhooks/{webhookId}/attempts");
            attempts.EnsureSuccessStatusCode();

            using var attemptsDocument = JsonDocument.Parse(await attempts.Content.ReadAsStringAsync());
            var attemptItems = attemptsDocument.RootElement.GetProperty("items");
            Assert.Equal(1, attemptItems.GetArrayLength());
            Assert.Equal(500, attemptItems[0].GetProperty("statusCode").GetInt32());
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    [Fact]
    public async Task Replay_stops_retrying_when_backoff_budget_is_exhausted()
    {
        await using var retryFactory = new RetryingApiFactory(_fixture.ConnectionString, maxAttempts: 4, backoffBaseSeconds: 15);
        var client = retryFactory.CreateClient();

        var (port, listener) = StartStubListener();
        try
        {
            var requestsServed = RespondWithStatuses(listener, 500);

            var slug = NewSlug();
            await _fixture.SeedEndpointAsync(slug, $"http://127.0.0.1:{port}/target");

            using var post = new StringContent("""{"orderId":456}""", Encoding.UTF8, "application/json");
            using var ingest = await client.PostAsync($"/hooks/{slug}", post);
            Assert.Equal(HttpStatusCode.NoContent, ingest.StatusCode);

            var webhookId = await GetSingleWebhookIdAsync(client, slug);

            var stopwatch = Stopwatch.StartNew();
            using var replay = await client.PostAsync($"/api/webhooks/{webhookId}/replay", null);
            stopwatch.Stop();
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

            using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(500, replayDocument.RootElement.GetProperty("statusCode").GetInt32());

            Assert.Equal(3, requestsServed());

            using var attempts = await client.GetAsync($"/api/webhooks/{webhookId}/attempts");
            attempts.EnsureSuccessStatusCode();

            using var attemptsDocument = JsonDocument.Parse(await attempts.Content.ReadAsStringAsync());
            Assert.Equal(3, attemptsDocument.RootElement.GetProperty("items").GetArrayLength());

            Assert.True(stopwatch.Elapsed.TotalSeconds >= 14.5);
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    [Fact]
    public async Task Replay_returns_502_and_records_every_transport_failure_when_retries_enabled()
    {
        await using var retryFactory = new RetryingApiFactory(_fixture.ConnectionString, maxAttempts: 3, backoffBaseSeconds: 0);
        var client = retryFactory.CreateClient();

        var slug = NewSlug();
        await _fixture.SeedEndpointAsync(slug, "http://127.0.0.1:59999/x");

        using var post = new StringContent("""{"orderId":789}""", Encoding.UTF8, "application/json");
        using var ingest = await client.PostAsync($"/hooks/{slug}", post);
        Assert.Equal(HttpStatusCode.NoContent, ingest.StatusCode);

        var webhookId = await GetSingleWebhookIdAsync(client, slug);

        using var replay = await client.PostAsync($"/api/webhooks/{webhookId}/replay", null);
        Assert.Equal(HttpStatusCode.BadGateway, replay.StatusCode);

        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, replayDocument.RootElement.GetProperty("statusCode").ValueKind);

        using var attempts = await client.GetAsync($"/api/webhooks/{webhookId}/attempts");
        attempts.EnsureSuccessStatusCode();

        using var attemptsDocument = JsonDocument.Parse(await attempts.Content.ReadAsStringAsync());
        var attemptItems = attemptsDocument.RootElement.GetProperty("items");
        Assert.Equal(3, attemptItems.GetArrayLength());
        foreach (var attempt in attemptItems.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, attempt.GetProperty("statusCode").ValueKind);
        }
    }

    private async Task<Guid> GetSingleWebhookIdAsync(HttpClient client, string slug)
    {
        var endpoints = await client.GetAsync("/api/endpoints");
        endpoints.EnsureSuccessStatusCode();

        using var endpointsDocument = JsonDocument.Parse(await endpoints.Content.ReadAsStringAsync());
        var endpointId = endpointsDocument.RootElement.GetProperty("items")
            .EnumerateArray()
            .First(item => item.GetProperty("slug").GetString() == slug)
            .GetProperty("id")
            .GetString();

        var webhooks = await client.GetAsync($"/api/endpoints/{endpointId}/webhooks");
        webhooks.EnsureSuccessStatusCode();

        using var webhooksDocument = JsonDocument.Parse(await webhooks.Content.ReadAsStringAsync());
        return Guid.Parse(webhooksDocument.RootElement.GetProperty("items")[0].GetProperty("id").GetString()!);
    }

    private static (int Port, HttpListener Listener) StartStubListener()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = FindFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (port, listener);
            }
            catch (HttpListenerException) when (attempt < 2)
            {
                listener.Close();
            }
        }
    }

    private static int FindFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static Task RespondWithNoContentOnce(HttpListener listener) => Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        context.Response.StatusCode = 204;
        context.Response.Close();
    });

    private static TaskCompletionSource<(string Path, string Body)> CaptureOneRequestOnce(
        HttpListener listener)
    {
        var captured = new TaskCompletionSource<(string Path, string Body)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            captured.SetResult((context.Request.Url?.PathAndQuery ?? "/", body));
            context.Response.StatusCode = 204;
            context.Response.Close();
        });
        return captured;
    }

    private static Func<int> RespondWithStatuses(HttpListener listener, params int[] statusCodes)
    {
        var requestsServed = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                var statusCodeIndex = Math.Min(requestsServed, statusCodes.Length - 1);
                requestsServed++;
                context.Response.StatusCode = statusCodes[statusCodeIndex];
                context.Response.Close();
            }
        });
        return () => requestsServed;
    }

    private sealed class RetryingApiFactory(string connectionString, int maxAttempts, int backoffBaseSeconds)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.UseSetting("Retry:MaxAttempts", maxAttempts.ToString());
            builder.UseSetting("Retry:BackoffBaseSeconds", backoffBaseSeconds.ToString());
        }
    }
}
