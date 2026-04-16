using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Encryption;

/// <summary>Verifies single-quote passwords are escaped before <c>PRAGMA key</c> interpolation.</summary>
internal class EncryptionSpecialCharsPasswordTest(
    IDbContextFactory<TodoDbContext> factory,
    ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    private const string EncryptedDb = "Encrypted_SpecialChars.db";
    // Single quote tests JS escaping in sqlite-worker.ts
    private const string Password = "it's a key";

    public override string Name => "Encryption_SpecialCharsPassword";

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
                Title = "Special Chars Todo",
                Description = "Password contains a single quote",
                IsCompleted = false,
                UpdatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        await DatabaseService.CloseDatabaseAsync(EncryptedDb);

        // Phase 2: reopen + verify
        await using (var ctx = CreateEncryptedContext())
        {
            var item = await ctx.TodoItems.FindAsync(id);
            if (item is null)
                throw new InvalidOperationException("Record not found after reopen with special-char password");
            if (item.Title != "Special Chars Todo")
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
