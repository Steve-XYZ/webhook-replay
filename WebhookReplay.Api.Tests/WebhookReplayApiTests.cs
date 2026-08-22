using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
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

    [Fact]
    public async Task ReceiveWebhook_with_valid_signature_returns_204_and_stores_request()
    {
        const string body = """{"orderId":123,"status":"paid"}""";
        var slug = NewSlug("hmac-valid");
        const string secret = "whsec-it-valid";
        await _fixture.SeedEndpointAsync(slug, "http://127.0.0.1:1/unused", secret);

        var client = _fixture.CreateClient();
        using var response = await PostIngestAsync(client, slug, body, $"sha256={ComputeHmacHex(secret, body)}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var webhookId = await GetSingleWebhookIdAsync(client, slug);

        using var detail = await client.GetAsync($"/api/webhooks/{webhookId}");
        using var document = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal(body, document.RootElement.GetProperty("bodyText").GetString());
    }

    [Fact]
    public async Task ReceiveWebhook_with_invalid_signature_returns_401_and_is_not_stored()
    {
        var slug = NewSlug("hmac-invalid");
        var endpointId = await _fixture.SeedEndpointAsync(
            slug, "http://127.0.0.1:1/unused", "whsec-it-invalid");

        var client = _fixture.CreateClient();
        using var wrongDigest = await PostIngestAsync(client, slug, """{"orderId":1}""", "sha256=" + new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongDigest.StatusCode);

        using var malformed = await PostIngestAsync(client, slug, """{"orderId":1}""", "not-a-signature");
        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);

        Assert.Equal(0, await CountWebhooksAsync(client, endpointId));
    }

    [Fact]
    public async Task ReceiveWebhook_with_missing_signature_header_on_secret_endpoint_returns_401_and_is_not_stored()
    {
        var slug = NewSlug("hmac-missing");
        var endpointId = await _fixture.SeedEndpointAsync(
            slug, "http://127.0.0.1:1/unused", "whsec-it-missing");

        var client = _fixture.CreateClient();
        using var post = new StringContent("""{"orderId":2}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"/hooks/{slug}", post);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal(0, await CountWebhooksAsync(client, endpointId));
    }

    [Fact]
    public async Task ReceiveWebhook_unsigned_request_against_no_secret_endpoint_still_works()
    {
        var slug = NewSlug("hmac-nosecret");
        var endpointId = await _fixture.SeedEndpointAsync(slug, "http://127.0.0.1:1/unused");

        var client = _fixture.CreateClient();
        using var post = new StringContent("""{"orderId":3}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"/hooks/{slug}", post);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(1, await CountWebhooksAsync(client, endpointId));
    }

    [Fact]
    public async Task CreateEndpoint_echoes_secret_only_in_creation_response()
    {
        var slug = NewSlug("hmac-echo");
        var client = _fixture.CreateClient();

        using var content = new StringContent(
            JsonSerializer.Serialize(new
            {
                name = "Echo",
                slug,
                forwardUrl = "http://127.0.0.1:1/x",
                secret = "whsec-it-echo"
            }),
            Encoding.UTF8,
            "application/json");
        using var created = await client.PostAsync("/api/endpoints", content);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal("whsec-it-echo", createdDocument.RootElement.GetProperty("secret").GetString());
        var id = createdDocument.RootElement.GetProperty("id").GetString();

        using var fetched = await client.GetAsync($"/api/endpoints/{id}");
        using var fetchedDocument = JsonDocument.Parse(await fetched.Content.ReadAsStringAsync());
        Assert.False(fetchedDocument.RootElement.TryGetProperty("secret", out _));

        using var list = await client.GetAsync("/api/endpoints");
        using var listDocument = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.DoesNotContain(
            listDocument.RootElement.GetProperty("items").EnumerateArray(),
            item => item.TryGetProperty("secret", out _));
    }

    private static string NewSlug() => $"it-{Guid.NewGuid():N}";

    private static string NewSlug(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static async Task<HttpResponseMessage> PostIngestAsync(
        HttpClient client,
        string slug,
        string body,
        string signature)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/hooks/{slug}") { Content = content };
        request.Headers.Add("X-Webhook-Signature", signature);
        return await client.SendAsync(request);
    }

    private static string ComputeHmacHex(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    private async Task<int> CountWebhooksAsync(HttpClient client, Guid endpointId)
    {
        using var webhooks = await client.GetAsync($"/api/endpoints/{endpointId}/webhooks");
        webhooks.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await webhooks.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").GetArrayLength();
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
}
