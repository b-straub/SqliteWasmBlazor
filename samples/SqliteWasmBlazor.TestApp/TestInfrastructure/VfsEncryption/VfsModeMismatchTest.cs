using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Two negatives to prove mode-mismatch failures are clean:
///   (a) plain DB opened with a key → decrypt/auth fails on first page read
///       (typically SQLITE_IOERR)
///   (b) encrypted DB opened without a key → SQLite can't parse the ciphertext
///       as a valid database (typically SQLITE_NOTADB)
/// Uses raw <see cref="SqliteWasmConnection"/> instances rather than the
/// DbContextFactory so each case controls its own key state.
/// </summary>
internal sealed class VfsModeMismatchTest(
    IDbContextFactory<EncryptedTestContext> factory,
    ISqliteWasmDatabaseService databaseService,
    IEncryptedSqliteWasmDatabaseService session)
    : VfsEncryptionTestBase(factory, databaseService, session)
{
    public override string Name => "VFS_ModeMismatch";

    // Use distinct DB file names so the two sub-scenarios never reuse a slot.
    private const string PlainDbName = "VfsModeTest_Plain.db";
    private const string EncryptedDbName = "VfsModeTest_Encrypted.db";

    protected override async Task EnsureFreshDatabaseAsync()
    {
        // Override base behavior — we manage multiple DB files ourselves.
        await DatabaseService.DeleteDatabaseAsync(PlainDbName).ContinueWith(_ => { });
        await DatabaseService.DeleteDatabaseAsync(EncryptedDbName).ContinueWith(_ => { });
        Console.WriteLine($"[{Name}] Cleared plain + encrypted mode-test DBs");
    }

    public override async ValueTask<string?> RunTestAsync()
    {
        var caseA = await RunPlainReopenedWithKeyAsync();
        if (caseA is not null) return $"case(a): {caseA}";

        var caseB = await RunEncryptedReopenedWithoutKeyAsync();
        if (caseB is not null) return $"case(b): {caseB}";

        return null;
    }

    private async Task<string?> RunPlainReopenedWithKeyAsync()
    {
        // Populate a plain DB. ClearEncryptionKey closes any prior open
        // DBs and routes the next xOpen through the vendor plain path.
        await Session.LockAsync();
        var plainConn = new SqliteWasmConnection($"Data Source={PlainDbName}");
        try
        {
            await plainConn.OpenAsync(CancellationToken.None);
            using var cmd = plainConn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(x INTEGER); INSERT INTO t VALUES (42);";
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            try { await plainConn.CloseAsync(); } catch { }
        }

        // Reopen same file WITH a key — SetEncryptionKey closes the plain
        // DB above and the next xOpen re-stamps file.key=TestKey.
        await Session.UnlockAsync(TestKey);
        var keyedConn = new SqliteWasmConnection($"Data Source={PlainDbName}");
        try
        {
            await keyedConn.OpenAsync(CancellationToken.None);
            using var cmd = keyedConn.CreateCommand();
            cmd.CommandText = "SELECT x FROM t";
            try
            {
                var v = await cmd.ExecuteScalarAsync();
                return $"plain-reopened-with-key unexpectedly returned x={v}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Name}] case(a) failed as expected: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{Name}] case(a) Open failed as expected: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            try { await keyedConn.CloseAsync(); } catch { }
            try { await DatabaseService.CloseDatabaseAsync(PlainDbName); } catch { }
        }
    }

    private async Task<string?> RunEncryptedReopenedWithoutKeyAsync()
    {
        // Populate an encrypted DB — install globalKey, then create.
        await Session.UnlockAsync(TestKey);
        var encConn = new SqliteWasmConnection($"Data Source={EncryptedDbName}");
        try
        {
            await encConn.OpenAsync(CancellationToken.None);
            using var cmd = encConn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(x INTEGER); INSERT INTO t VALUES (99);";
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            try { await encConn.CloseAsync(); } catch { }
        }

        // Reopen same file WITHOUT a key — ClearEncryptionKey closes the
        // encrypted DB above and the next xOpen routes through vendor
        // pass-through; SQLite sees random bytes where the format-3
        // header should be.
        await Session.LockAsync();
        var plainConn = new SqliteWasmConnection($"Data Source={EncryptedDbName}");
        try
        {
            await plainConn.OpenAsync(CancellationToken.None);
            using var cmd = plainConn.CreateCommand();
            cmd.CommandText = "SELECT x FROM t";
            try
            {
                var v = await cmd.ExecuteScalarAsync();
                return $"encrypted-reopened-without-key unexpectedly returned x={v}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Name}] case(b) failed as expected: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{Name}] case(b) Open failed as expected: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            try { await plainConn.CloseAsync(); } catch { }
            // SetEncryptionKey closes the plain handle above as part of
            // restoring the canonical TestKey for downstream tests.
            try { await Session.UnlockAsync(TestKey); } catch { }
        }
    }
}
