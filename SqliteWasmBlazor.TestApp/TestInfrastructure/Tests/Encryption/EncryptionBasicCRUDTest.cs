using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Encryption;

/// <summary>Verifies <c>Password=</c> is forwarded as <c>PRAGMA key</c>.</summary>
internal class EncryptionBasicCRUDTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    private const string EncryptedDb = "Encrypted_BasicCRUD.db";
    private const string Password = "test-sqlcipher-key-42";

    public override string Name => "Encryption_BasicCRUD";

    // Own lifecycle: uses a separate DB, not the shared TestDb.db
    protected override bool AutoCreateDatabase => false;

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");

        if (await DatabaseService.ExistsDatabaseAsync(EncryptedDb))
            await DatabaseService.DeleteDatabaseAsync(EncryptedDb);

        var id = Guid.NewGuid();

        // Phase 1: create + insert
        await using (var ctx = CreateEncryptedContext())
        {
            await ctx.Database.EnsureCreatedAsync();

            ctx.TodoItems.Add(new TodoItem
            {
                Id = id,
                Title = "Encrypted Todo",
                Description = "Written with PRAGMA key",
                IsCompleted = false,
                UpdatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        // Close so next open re-applies the key
        await DatabaseService.CloseDatabaseAsync(EncryptedDb);

        // Phase 2: reopen + verify
        await using (var ctx = CreateEncryptedContext())
        {
            var item = await ctx.TodoItems.FindAsync(id);
            if (item is null)
                throw new InvalidOperationException("Encrypted record not found after reopen");
            if (item.Title != "Encrypted Todo")
                throw new InvalidOperationException($"Unexpected title: {item.Title}");
        }

        await DatabaseService.CloseDatabaseAsync(EncryptedDb);
        await DatabaseService.DeleteDatabaseAsync(EncryptedDb);

        return "OK";
    }

    private TodoDbContext CreateEncryptedContext()
    {
        var connection = new SqliteWasmConnection($"Data Source={EncryptedDb};Password={Password}");
        var options = new DbContextOptionsBuilder<TodoDbContext>()
            .UseSqliteWasm(connection)
            .Options;
        return new TodoDbContext(options);
    }
}
