using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace WebhookReplay.Api.Tests;

public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17")
        .Build();

    public Task InitializeAsync() => _db.StartAsync();

    public new async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _db.GetConnectionString());
    }

    public async Task<Guid> SeedEndpointAsync(string slug, string forwardUrl)
    {
        var dataSource = Services.GetRequiredService<NpgsqlDataSource>();
        var endpointId = Guid.CreateVersion7();

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO endpoints (id, name, slug, forward_url)
            VALUES (@id, @name, @slug, @forward_url)
            """,
            connection);
        command.Parameters.AddWithValue("id", endpointId);
        command.Parameters.AddWithValue("name", $"Endpoint {slug}");
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("forward_url", forwardUrl);
        await command.ExecuteNonQueryAsync();

        return endpointId;
    }

    public async Task<Guid> SeedWebhookAsync(Guid endpointId, string bodyText, DateTime receivedAtUtc)
    {
        var dataSource = Services.GetRequiredService<NpgsqlDataSource>();
        var webhookId = Guid.CreateVersion7();

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO webhook_requests (id, endpoint_id, method, headers, body_text, received_at)
            VALUES (@id, @endpoint_id, @method, @headers, @body_text, @received_at)
            """,
            connection);
        command.Parameters.Add("id", NpgsqlTypes.NpgsqlDbType.Uuid).Value = webhookId;
        command.Parameters.Add("endpoint_id", NpgsqlTypes.NpgsqlDbType.Uuid).Value = endpointId;
        command.Parameters.Add("method", NpgsqlTypes.NpgsqlDbType.Text).Value = "POST";
        command.Parameters.Add("headers", NpgsqlTypes.NpgsqlDbType.Jsonb).Value = "{}";
        command.Parameters.Add("body_text", NpgsqlTypes.NpgsqlDbType.Text).Value = bodyText;
        command.Parameters.Add("received_at", NpgsqlTypes.NpgsqlDbType.TimestampTz).Value = receivedAtUtc;
        await command.ExecuteNonQueryAsync();

        return webhookId;
    }
}

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
