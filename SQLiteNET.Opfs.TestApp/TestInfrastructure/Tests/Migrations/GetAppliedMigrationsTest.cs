using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.Migrations;

/// <summary>
/// Test: Query applied migrations using EF Core API
/// Verifies Database.GetAppliedMigrationsAsync() works correctly
/// </summary>
internal class GetAppliedMigrationsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Migration_GetAppliedMigrations";

    // Migration tests manage their own database lifecycle
    protected override bool AutoCreateDatabase => false;

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // Before any migrations
        var migrationsBeforeCreate = await context.Database.GetAppliedMigrationsAsync();
        var countBefore = migrationsBeforeCreate.Count();

        if (countBefore != 0)
        {
            throw new InvalidOperationException($"Expected 0 migrations before creation, got {countBefore}");
        }

        // Apply migrations
        await context.Database.MigrateAsync();

        // After migrations
        var migrationsAfter = await context.Database.GetAppliedMigrationsAsync();
        var appliedMigrations = migrationsAfter.ToList();

        Console.WriteLine($"[GetAppliedMigrationsTest] Applied migrations count: {appliedMigrations.Count}");
        foreach (var migration in appliedMigrations)
        {
            Console.WriteLine($"  - {migration}");
        }

        // Verify GetPendingMigrationsAsync returns empty list
        var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
        var pendingCount = pendingMigrations.Count();

        if (pendingCount != 0)
        {
            throw new InvalidOperationException($"Expected 0 pending migrations after MigrateAsync, got {pendingCount}");
        }

        // Call MigrateAsync again - should not add duplicates
        await context.Database.MigrateAsync();

        var migrationsAfterSecond = await context.Database.GetAppliedMigrationsAsync();
        var countAfterSecond = migrationsAfterSecond.Count();

        if (countAfterSecond != appliedMigrations.Count)
        {
            throw new InvalidOperationException($"Migration count changed after second MigrateAsync: {appliedMigrations.Count} → {countAfterSecond}");
        }

        return "OK";
    }
}
