using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Create an encrypted DB with key K, insert rows, close. Then open the
/// same DB file with a different key K' via a direct <c>SqliteWasmConnection</c>
/// (bypassing the DbContextFactory's cached key). Executing any SQL must
/// fail — AEAD auth on the first page read rejects a wrong key — and no
/// plaintext must leak through error paths.
/// </summary>
internal sealed class VfsWrongKeyFailsTest(
    IDbContextFactory<EncryptedTestContext> factory,
    ISqliteWasmDatabaseService databaseService,
    IEncryptedSqliteWasmDatabaseService session)
    : VfsEncryptionTestBase(factory, databaseService, session)
{
    public override string Name => "VFS_WrongKeyFails";

    public override async ValueTask<string?> RunTestAsync()
    {
        // Seed with correct key (the factory holds it).
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            ctx.Items.Add(new VfsTestItem { Marker = "original", Payload = "original-payload" });
            await ctx.SaveChangesAsync();
        }
        await DatabaseService.CloseDatabaseAsync(EncryptedDatabaseName);

        // Reopen with a flipped key. Single-key model: install the wrong
        // key as the worker's global key, then open + read must fail
        // (AEAD on slot 0 rejects K').
        var wrongKey = (byte[])TestKey.Clone();
        wrongKey[0] ^= 0x01;
        await Session.UnlockAsync(wrongKey);

        var conn = new SqliteWasmConnection($"Data Source={EncryptedDatabaseName}");

        try
        {
            await conn.OpenAsync(CancellationToken.None);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Marker FROM VfsTestItems LIMIT 1";

            try
            {
                var result = await cmd.ExecuteScalarAsync();
                return $"FAIL: wrong-key open should have failed, but SELECT returned '{result}'";
            }
            catch (Exception ex)
            {
                var msg = ex.ToString();
                // Negative assertion: our test marker must NOT appear in the
                // error path — the error must not leak decrypted plaintext.
                if (msg.Contains("original", StringComparison.Ordinal))
                {
                    return $"FAIL: plaintext 'original' leaked into wrong-key error: {msg}";
                }
                Console.WriteLine($"[{Name}] Wrong-key SELECT failed as expected: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
        catch (Exception ex)
        {
            // Some paths may fail at Open() before reaching SQL. That's fine —
            // any clean failure without plaintext leak is a pass.
            var msg = ex.ToString();
            if (msg.Contains("original", StringComparison.Ordinal))
            {
                return $"FAIL: plaintext 'original' leaked into wrong-key Open error: {msg}";
            }
            Console.WriteLine($"[{Name}] Wrong-key Open failed as expected: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            try { await conn.CloseAsync(); } catch { /* ignore */ }
            // SetEncryptionKey implicitly closes any stale handle (incl. the
            // wrong-key one above) before swapping globalKey, so a single
            // call restores the canonical TestKey for downstream tests.
            try { await Session.UnlockAsync(TestKey); } catch { /* ignore */ }
        }
    }
}
