using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Base class for PRF-VFS integration tests. Owns the fixed 32-byte test
/// key and the database name shared across the suite. Each test starts with
/// a fresh database via <see cref="EnsureFreshDatabaseAsync"/>.
/// </summary>
internal abstract class VfsEncryptionTestBase
{
    public const string EncryptedDatabaseName = "EncryptedTestDb.db";

    /// <summary>
    /// Deterministic 32-byte test key. The byte pattern is identifiable in
    /// hex dumps and is NOT a valid SQLite header — using it as a "plaintext"
    /// scan marker also serves as a sanity check that nothing in the worker
    /// echoes the key back out.
    /// </summary>
    public static readonly byte[] TestKey = BuildTestKey();

    public abstract string Name { get; }

    protected IDbContextFactory<EncryptedTestContext> Factory { get; }
    protected ISqliteWasmDatabaseService DatabaseService { get; }
    protected IEncryptedSqliteWasmDatabaseService Session { get; }

    protected VfsEncryptionTestBase(
        IDbContextFactory<EncryptedTestContext> factory,
        ISqliteWasmDatabaseService databaseService,
        IEncryptedSqliteWasmDatabaseService session)
    {
        Factory = factory;
        DatabaseService = databaseService;
        Session = session;
    }

    public async ValueTask<string?> RunTestWithFreshDatabaseAsync()
    {
        await EnsureFreshDatabaseAsync();
        return await RunTestAsync();
    }

    public abstract ValueTask<string?> RunTestAsync();

    protected virtual async Task EnsureFreshDatabaseAsync()
    {
        // Session.UnlockAsync is the canonical entry-point: it force-closes
        // every currently-open DB (so a synthetic-PRF test's OFile under a
        // different key can't survive into this test) and writes the OPFS
        // lock marker. Subsequent xOpens re-stamp file.key from globalKey
        // = TestKey.
        await Session.UnlockAsync(TestKey);

        await using var ctx = await Factory.CreateDbContextAsync();
        await ctx.Database.EnsureDeletedAsync();
        await ctx.Database.EnsureCreatedAsync();
        Console.WriteLine($"[{Name}] Fresh {EncryptedDatabaseName} created");
    }

    private static byte[] BuildTestKey()
    {
        var key = new byte[32];
        for (var i = 0; i < 32; i++)
        {
            key[i] = (byte)(0xA0 + i);
        }
        return key;
    }
}
