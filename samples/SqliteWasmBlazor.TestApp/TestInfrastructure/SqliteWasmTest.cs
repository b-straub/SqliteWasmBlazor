using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure;

internal abstract class SqliteWasmTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService? databaseService = null)
{
    public abstract string Name { get; }

    protected IDbContextFactory<TodoDbContext> Factory { get; } = factory;
    protected ISqliteWasmDatabaseService? DatabaseService { get; } = databaseService;

    /// <summary>
    /// Override this to skip automatic database creation for tests that manage their own database lifecycle.
    /// Defaults to true. Set to false for migration tests that need to test database creation.
    /// </summary>
    protected virtual bool AutoCreateDatabase => true;

    public async ValueTask<string?> RunTestWithFreshDatabaseAsync()
    {
        // Ensure fresh database before each test (unless test manages its own lifecycle)
        if (AutoCreateDatabase)
        {
            await EnsureFreshDatabaseAsync();
        }
        else
        {
            // Just ensure deleted for tests that manage their own creation
            await EnsureDeletedOnlyAsync();
        }

        // Run the actual test
        return await RunTestAsync();
    }

    public abstract ValueTask<string?> RunTestAsync();

    private async Task EnsureFreshDatabaseAsync()
    {
        try
        {
            await using var context = await Factory.CreateDbContextAsync();

            // Delete and recreate database for fresh state. Migrated, not
            // EnsureCreated: the file outlives the page in OPFS, and a schema
            // without its history table is what the next boot's migration
            // trips over — a table that already exists, reported as
            // SCHEMA_INCOMPATIBLE before the harness has even wiped the pool.
            await context.Database.EnsureDeletedAsync();
            await context.Database.MigrateAsync();

            Console.WriteLine($"[{Name}] Fresh database created");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Name}] Failed to create fresh database: {ex.Message}");
            throw;
        }
    }

    private async Task EnsureDeletedOnlyAsync()
    {
        try
        {
            await using var context = await Factory.CreateDbContextAsync();

            // Only delete - test will handle creation
            await context.Database.EnsureDeletedAsync();

            Console.WriteLine($"[{Name}] Database deleted (test manages creation)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Name}] Failed to delete database: {ex.Message}");
            throw;
        }
    }
}
