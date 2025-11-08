using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure;

internal abstract class SqliteWasmTest(IDbContextFactory<TodoDbContext> factory)
{
    public abstract string Name { get; }

    protected IDbContextFactory<TodoDbContext> Factory { get; } = factory;

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

            // Delete and recreate database for fresh state
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();

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
