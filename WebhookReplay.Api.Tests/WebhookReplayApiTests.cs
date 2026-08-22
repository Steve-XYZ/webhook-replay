using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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

    private static string NewSlug() => $"it-{Guid.NewGuid():N}";

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
}
