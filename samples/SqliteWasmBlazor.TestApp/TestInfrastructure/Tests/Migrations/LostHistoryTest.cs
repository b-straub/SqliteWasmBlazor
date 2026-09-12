using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations;

/// <summary>
/// A database whose schema is intact but whose <c>__EFMigrationsHistory</c>
/// is gone reports <see cref="DbInitState.SCHEMA_INCOMPATIBLE"/> and asks for
/// a reset.
/// </summary>
/// <remarks>
/// <para>
/// With no history every migration is pending, so EF replays the first and
/// SQLite refuses to create a table that exists. The library does not try to
/// repair that: deciding which migrations "really" ran means guessing, and a
/// guess that records work as done which never happened leaves a database
/// nothing will ever come back to fix. The failure carries SQLite's own
/// reason so the reset prompt says what it found.
/// </para>
/// <para>
/// Drives <see cref="ISqliteWasmInitializer"/> end to end and inspects the
/// status surface, so it manages <c>TodoDb.db</c> itself and restores a
/// healthy schema plus READY on the way out for whatever runs next.
/// </para>
/// </remarks>
internal sealed class LostHistoryTest(IServiceProvider services)
{
    public string Name => "Migration_LostHistorySurfacesMismatch";

    private IDbContextFactory<TodoDbContext> Factory =>
        services.GetRequiredService<IDbContextFactory<TodoDbContext>>();

    private IDbInitializationReporter Reporter =>
        services.GetRequiredService<IDbInitializationReporter>();

    private IDbInitializationStatus Status =>
        services.GetRequiredService<IDbInitializationStatus>();

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
            await using var ctx = await Factory.CreateDbContextAsync();
            await ctx.Database.EnsureDeletedAsync();
            await ctx.Database.MigrateAsync();
            Reporter.Report(DbInitState.READY);
        }
    }

    private async ValueTask<string?> RunTestAsync()
    {
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            await ctx.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"__EFMigrationsHistory\"");
        }

        // Reset is the claim that what is on disk has changed — it has.
        var initializer = services.GetRequiredService<ISqliteWasmInitializer>();
        initializer.Reset();
        await initializer.InitializeAsync();

        if (Status.State != DbInitState.SCHEMA_INCOMPATIBLE)
        {
            return $"FAIL: expected SCHEMA_INCOMPATIBLE, got {Status.State} " +
                   $"({Status.Failure?.GetType().Name ?? "no failure"})";
        }

        if (Status.Failure is not SchemaIncompatibleFailure failure)
        {
            return $"FAIL: expected SchemaIncompatibleFailure, got " +
                   $"{Status.Failure?.GetType().Name ?? "null"}";
        }

        // The reason is SQLite's, and it should name what it tripped over.
        if (!failure.Reason.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return $"FAIL: expected the reason to say what already exists, got '{failure.Reason}'";
        }

        return null;
    }
}
