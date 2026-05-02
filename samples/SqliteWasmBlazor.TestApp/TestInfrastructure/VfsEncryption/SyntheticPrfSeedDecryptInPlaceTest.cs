using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Abstractions;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// In-place encrypted → plain composition test. Materialises an encrypted
/// DB under a synthetic-seed-derived K, writes rows, runs
/// <c>ISqliteWasmDatabaseService.DecryptDatabaseInPlaceAsync()</c>, then
/// reopens via the same context with no key registered to confirm:
///   1. The on-disk DB is now plain (opens without a registered key).
///   2. Rows survive the in-place transition byte-for-byte.
/// Symmetric to <c>SyntheticPrfSeedEncryptInPlaceTest</c>; together they
/// pin the plain↔encrypted in-place pair against the same caller-visible
/// schema and the same OPFS path.
/// </summary>
internal sealed class SyntheticPrfSeedDecryptInPlaceTest
{
    private const int RowCount = 25;
    private const string SyntheticKeyId = "prf-keys:synthetic-decrypt-in-place";

    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly ICryptoProvider _cryptoProvider;

    public string Name => "Synthetic_PrfSeed_DecryptInPlace_PreservesRowsAsPlain";

    public SyntheticPrfSeedDecryptInPlaceTest(
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

        var seed = new byte[32];
        for (var i = 0; i < 32; i++)
        {
            seed[i] = (byte)(0x55 + i);
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
            // Phase 1 — install K, materialise an encrypted DB, write rows.
            var installA = await _databaseService.InstallEncryptionKeyAsync(dbName, k);
            if (installA != VfsKeyInstallResult.NO_EXISTING_DB)
            {
                return $"FAIL: phase 1 install expected NO_EXISTING_DB, got {installA}";
            }
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                await ctx.Database.EnsureCreatedAsync();
                for (var i = 0; i < RowCount; i++)
                {
                    ctx.Items.Add(new VfsTestItem
                    {
                        Marker = $"decrypt-in-place-{i}",
                        Payload = $"payload-{i}-{Guid.NewGuid():N}",
                    });
                }
                await ctx.SaveChangesAsync();
            }

            // Phase 2 — in-place encrypted → plain. The worker snapshots K
            // (the registered key) before close, decrypts to plain pages
            // via rekeySlots(K→undefined), and writes plain pages back to
            // the same OPFS path. Bytes never cross C#↔JS.
            await _databaseService.DecryptDatabaseInPlaceAsync(dbName);

            // Phase 3 — reopen plain. The worker registry was cleared by
            // the implicit close inside DecryptInPlace; opening with no
            // key registered must succeed and return the plain pages.
            List<VfsTestItem> rows;
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                rows = await ctx.Items.OrderBy(x => x.Id).ToListAsync();
            }

            if (rows.Count != RowCount)
            {
                return $"FAIL: expected {RowCount} rows after DecryptInPlace, got {rows.Count}";
            }
            for (var i = 0; i < RowCount; i++)
            {
                if (rows[i].Marker != $"decrypt-in-place-{i}")
                {
                    return $"FAIL: row {i} Marker mismatch (got '{rows[i].Marker}')";
                }
                if (!rows[i].Payload.StartsWith($"payload-{i}-", StringComparison.Ordinal))
                {
                    return $"FAIL: row {i} Payload mismatch (got '{rows[i].Payload}')";
                }
            }

            // Phase 4 — sanity check that the DB is genuinely plain now,
            // not just opening through some cached state. Re-installing K
            // would fail AEAD on slot 0 since slot 0 is now plain pages
            // — confirm the worker rejects it.
            await _databaseService.CloseDatabaseAsync(dbName);
            var rejectK = await _databaseService.InstallEncryptionKeyAsync(dbName, k);
            if (rejectK != VfsKeyInstallResult.WRONG_KEY)
            {
                return $"FAIL: post-decrypt install with old K expected WRONG_KEY (slot 0 is now plain), got {rejectK}";
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
