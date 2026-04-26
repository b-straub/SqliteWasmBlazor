using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations.Recovery;

/// <summary>
/// Base for browser-based tests that drive
/// <c>InitializeSqliteWasmDatabaseAsync&lt;TodoDbContext&gt;</c> end-to-end
/// and inspect the typed boot status surface. Shares the live worker bridge
/// and singleton <see cref="DbInitializationService"/> with the rest of the
/// app — each test resets the reporter before invoking the helper and
/// re-migrates the DB on teardown so the next test starts clean.
/// </summary>
internal abstract class MigrationRecoveryTestBase
{
    protected IServiceProvider Services { get; }
    protected IDbContextFactory<TodoDbContext> Factory { get; }
    protected IDbInitializationReporter Reporter { get; }
    protected IDbInitializationStatus Status { get; }

    public abstract string Name { get; }

    protected MigrationRecoveryTestBase(IServiceProvider services)
    {
        Services = services;
        Factory = services.GetRequiredService<IDbContextFactory<TodoDbContext>>();
        Reporter = services.GetRequiredService<IDbInitializationReporter>();
        Status = services.GetRequiredService<IDbInitializationStatus>();
    }

    public async ValueTask<string?> RunTestWithFreshDatabaseAsync()
    {
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureDeletedAsync();
            await ctx.Database.MigrateAsync();
        }

        try
        {
            return await RunTestAsync();
        }
        finally
        {
            // Restore: any subsequent test or app code that resolves
            // TodoDbContext expects a healthy schema and READY status.
            try
            {
                await using var ctx = await Factory.CreateDbContextAsync();
                await ctx.Database.EnsureDeletedAsync();
                await ctx.Database.MigrateAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{Name}] teardown re-migrate failed: {ex.Message}");
            }
            Reporter.Report(DbInitState.READY);
        }
    }

    public abstract ValueTask<string?> RunTestAsync();

    /// <summary>
    /// Drop the migrations history table so the next <c>MigrateAsync</c> sees
    /// every migration as pending and throws "already exists" on the first
    /// CREATE TABLE — this is the only way to force the recovery path from
    /// the public <see cref="SqliteWasmServiceCollectionExtensions"/> helper.
    /// </summary>
    protected static async Task DropMigrationsHistoryAsync(TodoDbContext ctx)
    {
        await ctx.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"__EFMigrationsHistory\"");
    }

    /// <summary>
    /// Drop a real entity column so the recovery probe surfaces a
    /// <see cref="SchemaMismatch"/> with <see cref="SchemaMismatch.MissingColumn"/>
    /// set. Uses SQLite 3.35+ ALTER TABLE DROP COLUMN.
    /// </summary>
    protected static async Task DropColumnAsync(TodoDbContext ctx, string table, string column)
    {
        // Identifiers can't be parameterized; tests pass compile-time constants.
#pragma warning disable EF1002
        await ctx.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\"");
#pragma warning restore EF1002
    }

    /// <summary>
    /// Add a column the model does not declare so the recovery probe surfaces
    /// a <see cref="SchemaMismatch"/> with <see cref="SchemaMismatch.ExtraColumn"/>
    /// set.
    /// </summary>
    protected static async Task AddColumnAsync(TodoDbContext ctx, string table, string column)
    {
#pragma warning disable EF1002
        await ctx.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" TEXT");
#pragma warning restore EF1002
    }

    /// <summary>
    /// Reset the boot reporter to <see cref="DbInitState.NOT_STARTED"/> and
    /// drive the typed initialization helper. The early-return guard in the
    /// helper only triggers on terminal failure states, so READY → NOT_STARTED
    /// is needed to re-enter the path.
    /// </summary>
    protected async Task DriveBootAsync()
    {
        Reporter.Report(DbInitState.NOT_STARTED);
        await Services.InitializeSqliteWasmDatabaseAsync<TodoDbContext>();
    }
}
