using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Encryption;

/// <summary>
/// Verifies that opening an encrypted database with the wrong password (or no password)
/// throws an exception rather than silently succeeding with corrupt/empty data.
/// </summary>
internal class EncryptionWrongPasswordTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    private const string EncryptedDb = "Encrypted_WrongPassword.db";
    private const string CorrectPassword = "correct-password-42";
    private const string WrongPassword = "wrong-password";

    public override string Name => "Encryption_WrongPassword";

    protected override bool AutoCreateDatabase => false;

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");

        if (await DatabaseService.ExistsDatabaseAsync(EncryptedDb))
            await DatabaseService.DeleteDatabaseAsync(EncryptedDb);

        // Phase 1: create encrypted database with the correct key
        await using (var ctx = CreateContext(CorrectPassword))
        {
            await ctx.Database.EnsureCreatedAsync();

            ctx.TodoItems.Add(new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = "Secret",
                Description = "Protected data",
                IsCompleted = false,
                UpdatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        await DatabaseService.CloseDatabaseAsync(EncryptedDb);

        // Phase 2: attempt to open with the wrong key — must throw
        bool threw = false;
        try
        {
            await using var ctx = CreateContext(WrongPassword);
            // EnsureCreatedAsync triggers the worker open + first query, which will fail when
            // SQLCipher tries to read the encrypted file with the wrong key.
            _ = await ctx.TodoItems.CountAsync();
        }
        catch
        {
            threw = true;
        }

        if (!threw)
            throw new InvalidOperationException(
                "Expected an exception when opening an encrypted database with the wrong password, but none was thrown.");

        await DatabaseService.CloseDatabaseAsync(EncryptedDb);
        await DatabaseService.DeleteDatabaseAsync(EncryptedDb);

        return "OK";
    }

    private TodoDbContext CreateContext(string password)
    {
        var connection = new SqliteWasmConnection($"Data Source={EncryptedDb};Password={password}");
        var options = new DbContextOptionsBuilder<TodoDbContext>()
            .UseSqliteWasm(connection)
            .Options;
        return new TodoDbContext(options);
    }
}
