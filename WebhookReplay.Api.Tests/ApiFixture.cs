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

    public async Task<Guid> SeedEndpointAsync(string slug, string forwardUrl, string? secret = null)
    {
        var dataSource = Services.GetRequiredService<NpgsqlDataSource>();
        var endpointId = Guid.CreateVersion7();

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO endpoints (id, name, slug, forward_url, secret)
            VALUES (@id, @name, @slug, @forward_url, @secret)
            """,
            connection);
        command.Parameters.AddWithValue("id", endpointId);
        command.Parameters.AddWithValue("name", $"Endpoint {slug}");
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("forward_url", forwardUrl);
        command.Parameters.AddWithValue("secret", (object?)secret ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();

        return endpointId;
    }
}

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
