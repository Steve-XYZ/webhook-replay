using Npgsql;
using NpgsqlTypes;

namespace WebhookReplay.Api.Features.Endpoints;

public static class ListEndpoints
{
    private const string SelectSql = """
        SELECT id, name, slug, forward_url, created_at
        FROM endpoints
        ORDER BY created_at DESC
        """;

    public static async Task<IResult> HandleAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var items = new List<object>();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SelectSql, connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new
            {
                id = reader.GetGuid(0),
                name = reader.GetString(1),
                slug = reader.GetString(2),
                forwardUrl = reader.GetString(3),
                createdAt = reader.GetDateTime(4)
            });
        }

        return Results.Ok(new { items });
    }
}
