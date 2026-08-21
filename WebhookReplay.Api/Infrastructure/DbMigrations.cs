using System.Reflection;
using Npgsql;

namespace WebhookReplay.Api.Infrastructure;

public static class DbMigrations
{
    private const string EnsureMigrationsTableSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            name       text PRIMARY KEY,
            applied_at timestamptz NOT NULL DEFAULT now()
        )
        """;

    public static async Task ApplyAsync(NpgsqlDataSource dataSource, ILogger logger)
    {
        await using var connection = await dataSource.OpenConnectionAsync();

        await using (var ensure = new NpgsqlCommand(EnsureMigrationsTableSql, connection))
        {
            await ensure.ExecuteNonQueryAsync();
        }

        foreach (var (name, sql) in ReadEmbeddedMigrations())
        {
            var alreadyApplied = false;
            await using (var check = new NpgsqlCommand(
                "SELECT COUNT(*) FROM schema_migrations WHERE name = @name", connection))
            {
                check.Parameters.AddWithValue("name", name);
                alreadyApplied = Convert.ToInt64(await check.ExecuteScalarAsync()) > 0;
            }

            if (alreadyApplied)
            {
                continue;
            }

            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using (var apply = new NpgsqlCommand(sql, connection, transaction))
                {
                    await apply.ExecuteNonQueryAsync();
                }

                await using (var record = new NpgsqlCommand(
                    "INSERT INTO schema_migrations (name) VALUES (@name)", connection, transaction))
                {
                    record.Parameters.AddWithValue("name", name);
                    await record.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
            }

            logger.LogInformation("Applied migration {MigrationName}", name);
        }
    }

    private static IEnumerable<(string Name, string Sql)> ReadEmbeddedMigrations() =>
        typeof(DbMigrations).Assembly
            .GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.") && name.EndsWith(".sql"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                const string marker = ".Migrations.";
                using var stream = typeof(DbMigrations).Assembly.GetManifestResourceStream(name)!
                    ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
                using var reader = new StreamReader(stream);
                return (Name: name[(name.IndexOf(marker) + marker.Length)..], Sql: reader.ReadToEnd());
            });
}
