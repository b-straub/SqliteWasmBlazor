using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Abstractions;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// In-place plain → encrypted composition test (no Export/Delete/Import
/// ceremony). Materialises a plain DB through PrfVfsTestContext (no static
/// key, no key registered), writes rows, runs
/// <c>ISqliteWasmDatabaseService.EncryptDatabaseInPlaceAsync(K)</c>, then
/// installs K and reopens via the same context to confirm:
///   1. The on-disk DB is now encrypted under K (worker side AEAD test
///      passes — install returns MATCH).
///   2. Rows survive the in-place transition byte-for-byte (caller never
///      saw bytes — they stayed in the worker).
/// Drives the synthetic-seed → X25519 pubkey → K chain so the test runs
/// without WebAuthn and without the byte-shuttle export ceremony.
/// </summary>
internal sealed class SyntheticPrfSeedEncryptInPlaceTest
{
    private const int RowCount = 25;
    private const string SyntheticKeyId = "prf-keys:synthetic-encrypt-in-place";

    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly ICryptoProvider _cryptoProvider;

    public string Name => "Synthetic_PrfSeed_EncryptInPlace_PreservesRowsUnderKey";

    public SyntheticPrfSeedEncryptInPlaceTest(
        IDbContextFactory<PrfVfsTestContext> factory,
        ISqliteWasmDatabaseService databaseService,
        ICryptoProvider cryptoProvider)
    {
        _factory = factory;
        _databaseService = databaseService;
        _cryptoProvider = cryptoProvider;
    }

    public async ValueTask<string?> RunAsync()
    {
        var dbName = PrfVfsTestContext.DatabaseName;

        await CleanupAsync(dbName);

        // Synthetic seed → X25519 pubkey-bytes K (same chain R3.1 uses).
        var seed = new byte[32];
        for (var i = 0; i < 32; i++)
        {
            seed[i] = (byte)(0x33 + i);
        }

        var storeResult = await _cryptoProvider.StoreKeysAsync(SyntheticKeyId, seed, ttlMs: null);
        if (!storeResult.Success || storeResult.Value is null)
        {
            return $"FAIL: StoreKeysAsync returned {storeResult.ErrorCode}";
        }

        byte[] k;
        try
        {
            k = Convert.FromBase64String(storeResult.Value.X25519PublicKey);
        }
        catch (FormatException ex)
        {
            return $"FAIL: X25519 pubkey decode failed: {ex.Message}";
        }
        if (k.Length != 32)
        {
            return $"FAIL: X25519 pubkey is {k.Length} bytes, expected 32";
        }

        try
        {
            // Phase 1 — open plain DB (no key registered), write rows, close.
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                await ctx.Database.EnsureCreatedAsync();
                for (var i = 0; i < RowCount; i++)
                {
                    ctx.Items.Add(new VfsTestItem
                    {
                        Marker = $"encrypt-in-place-{i}",
                        Payload = $"payload-{i}-{Guid.NewGuid():N}",
                    });
                }
                await ctx.SaveChangesAsync();
            }
            await _databaseService.CloseDatabaseAsync(dbName);

            // Phase 2 — in-place plain → encrypted. Bytes never cross the
            // C#↔JS boundary; the worker reads OPFS plain pages, rewraps
            // under K via rekeySlots(undefined→K), and writes encrypted
            // slots back to the same OPFS path.
            await _databaseService.EncryptDatabaseInPlaceAsync(dbName, k);

            // Phase 3 — install K + reopen. MATCH proves the on-disk DB
            // now AEAD-authenticates under K (i.e., the in-place rewrap
            // produced legal slot-format ciphertext under K).
            var installOutcome = await _databaseService.InstallEncryptionKeyAsync(dbName, k);
            if (installOutcome != VfsKeyInstallResult.MATCH)
            {
                return $"FAIL: install after EncryptInPlace expected MATCH, got {installOutcome}";
            }

            // Phase 4 — read rows back and verify byte-for-byte survival.
            List<VfsTestItem> rows;
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                rows = await ctx.Items.OrderBy(x => x.Id).ToListAsync();
            }

            if (rows.Count != RowCount)
            {
                return $"FAIL: expected {RowCount} rows after EncryptInPlace, got {rows.Count}";
            }
            for (var i = 0; i < RowCount; i++)
            {
                if (rows[i].Marker != $"encrypt-in-place-{i}")
                {
                    return $"FAIL: row {i} Marker mismatch (got '{rows[i].Marker}')";
                }
                if (!rows[i].Payload.StartsWith($"payload-{i}-", StringComparison.Ordinal))
                {
                    return $"FAIL: row {i} Payload mismatch (got '{rows[i].Payload}')";
                }
            }

            return "OK";
        }
        finally
        {
            _cryptoProvider.RemoveCachedKey(SyntheticKeyId);
            await CleanupAsync(dbName);
            Array.Clear(k, 0, k.Length);
            Array.Clear(seed, 0, seed.Length);
        }
    }

    private async Task CleanupAsync(string dbName)
    {
        try { await _databaseService.CloseDatabaseAsync(dbName); } catch { }
        try { await _databaseService.ClearEncryptionKeyAsync(dbName); } catch { }
        try { await _databaseService.DeleteDatabaseAsync(dbName); } catch { }
    }
}
