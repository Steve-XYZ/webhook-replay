using Npgsql;
using NpgsqlTypes;

namespace WebhookReplay.Api.Features.Endpoints;

public static class GetEndpoint
{
    private const string SelectSql = """
        SELECT id, name, slug, forward_url, created_at
        FROM endpoints
        WHERE id = @id
        """;

    public static async Task<IResult> HandleAsync(
        string id,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var endpointId))
        {
            return Results.NotFound(new { error = $"Endpoint '{id}' not found." });
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(SelectSql, connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = endpointId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return Results.NotFound(new { error = $"Endpoint '{id}' not found." });
        }

        return Results.Ok(new
        {
            id = reader.GetGuid(0),
            name = reader.GetString(1),
            slug = reader.GetString(2),
            forwardUrl = reader.GetString(3),
            createdAt = reader.GetDateTime(4)
        });
    }
}
