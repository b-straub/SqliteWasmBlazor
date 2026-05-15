using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

/// <summary>
/// Base class for CryptoSync integration tests.
/// Manages CryptoTestContext lifecycle alongside the worker database service.
/// </summary>
internal abstract class CryptoSyncTestBase(
    IDbContextFactory<CryptoTestContext> cryptoFactory,
    ISqliteWasmDatabaseService databaseService,
    IEncryptedSqliteWasmDatabaseService session)
{
    protected const string CryptoDatabaseName = "CryptoTestDb.db";

    public abstract string Name { get; }

    protected IDbContextFactory<CryptoTestContext> CryptoFactory { get; } = cryptoFactory;
    protected ISqliteWasmDatabaseService? DatabaseService { get; } = databaseService;
    protected IEncryptedSqliteWasmDatabaseService Session { get; } = session;

    public async ValueTask<string?> RunTestWithFreshDatabaseAsync()
    {
        await EnsureFreshDatabaseAsync();
        return await RunTestAsync();
    }

    public abstract ValueTask<string?> RunTestAsync();

    private async Task EnsureFreshDatabaseAsync()
    {
        // Session.UnlockAsync force-closes every currently-open DB before
        // swapping globalKey to CryptoTestContext.EncryptionKey. file.key
        // is stamped at xOpen and persists for the OFile lifetime, so this
        // close-and-swap sequence ensures the next xOpen sees a clean key.
        await Session.UnlockAsync(CryptoTestContext.EncryptionKey);

        await using var context = await CryptoFactory.CreateDbContextAsync();
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        Console.WriteLine($"[{Name}] Fresh CryptoTestDb created");
    }
}
