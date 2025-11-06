using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.Migrations;

/// <summary>
/// Test: Verify __EFMigrationsHistory table is created and tracks migrations
/// </summary>
internal class MigrationHistoryTableTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Migration_HistoryTableTracking";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // Ensure clean state
        await context.Database.EnsureDeletedAsync();

        // Apply migrations
        await context.Database.MigrateAsync();

        // Verify __EFMigrationsHistory table exists
        var tableExistsSql = @"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type='table' AND name='__EFMigrationsHistory'";

        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = tableExistsSql;
        var tableCount = (long)(await command.ExecuteScalarAsync() ?? 0L);

        if (tableCount != 1)
        {
            throw new InvalidOperationException("__EFMigrationsHistory table does not exist");
        }

        // Query migration history
        command.CommandText = "SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId";
        var migrations = new List<(string MigrationId, string ProductVersion)>();

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var migrationId = reader.GetString(0);
            var productVersion = reader.GetString(1);
            migrations.Add((migrationId, productVersion));
        }

        // Note: If no migrations exist yet (only EnsureCreated was used), this is expected
        // The test verifies the infrastructure works
        Console.WriteLine($"[MigrationHistoryTableTest] Found {migrations.Count} applied migrations");
        foreach (var (migrationId, productVersion) in migrations)
        {
            Console.WriteLine($"  - {migrationId} (EF {productVersion})");
        }

        // Verify calling MigrateAsync again doesn't duplicate entries
        await context.Database.MigrateAsync();

        command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
        var countAfterSecond = (long)(await command.ExecuteScalarAsync() ?? 0L);

        if (countAfterSecond != migrations.Count)
        {
            throw new InvalidOperationException($"Migration count changed: {migrations.Count} → {countAfterSecond}");
        }

        return "OK";
    }
}
