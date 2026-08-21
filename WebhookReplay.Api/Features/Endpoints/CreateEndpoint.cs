using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace WebhookReplay.Api.Features.Endpoints;

public static class CreateEndpoint
{
    private const string SlugPattern = "^[a-z0-9-]+$";

    private const string InsertSql = """
        INSERT INTO endpoints (id, name, slug, forward_url, created_at)
        VALUES (@id, @name, @slug, @forward_url, @created_at)
        """;

    public sealed record Request(string Name, string Slug, string ForwardUrl);

    public static async Task<IResult> HandleAsync(
        Request request,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Any(char.IsWhiteSpace))
        {
            return Results.BadRequest(new { error = "Name must be non-empty and contain no whitespace." });
        }

        if (!Regex.IsMatch(request.Slug, SlugPattern))
        {
            return Results.BadRequest(new { error = "Slug must match '^[a-z0-9-]+$'." });
        }

        if (!Uri.TryCreate(request.ForwardUrl, UriKind.Absolute, out var forwardUri) ||
            (forwardUri.Scheme != Uri.UriSchemeHttp && forwardUri.Scheme != Uri.UriSchemeHttps))
        {
            return Results.BadRequest(new { error = "Forward URL must be an absolute http(s) URL." });
        }

        var id = Guid.CreateVersion7();
        var createdAt = DateTime.UtcNow;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(InsertSql, connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        command.Parameters.Add("name", NpgsqlDbType.Text).Value = request.Name;
        command.Parameters.Add("slug", NpgsqlDbType.Text).Value = request.Slug;
        command.Parameters.Add("forward_url", NpgsqlDbType.Text).Value = request.ForwardUrl;
        command.Parameters.Add("created_at", NpgsqlDbType.TimestampTz).Value = createdAt;

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == "23505")
        {
            return Results.Conflict(new { error = $"Slug '{request.Slug}' already exists." });
        }

        return Results.Created($"/api/endpoints/{id}", new
        {
            id,
            name = request.Name,
            slug = request.Slug,
            forwardUrl = request.ForwardUrl,
            createdAt
        });
    }
}
