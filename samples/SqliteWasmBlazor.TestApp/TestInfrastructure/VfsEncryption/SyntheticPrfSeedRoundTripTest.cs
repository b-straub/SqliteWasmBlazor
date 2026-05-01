using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Abstractions;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// R3.1 composition test: drive a synthetic 32-byte PRF seed through the
/// production key-derivation chain (StoreKeysAsync → X25519 keypair →
/// pubkey-bytes K → InstallEncryptionKeyAsync → encrypted VFS), write rows,
/// reopen with the same K, and verify the rows survive. Replaces the
/// WebAuthn ceremony with a known seed so the same composition that
/// PrfVfsTest.razor exercises in the browser can be validated end-to-end
/// without virtual-authenticator infrastructure.
/// </summary>
internal sealed class SyntheticPrfSeedRoundTripTest
{
    private const int RowCount = 25;
    private const string SyntheticKeyId = "prf-keys:synthetic-seed-test";

    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly ICryptoProvider _cryptoProvider;

    public string Name => "Synthetic_PrfSeed_DrivesEncryptedVfsRoundTrip";

    public SyntheticPrfSeedRoundTripTest(
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

        // Ensure a clean slate — the demo page may have left state on disk.
        await CleanupAsync(dbName);

        // Synthetic seed in place of WebAuthn-PRF output. The byte pattern
        // is identifiable so any leak path would be obvious in a hex dump.
        var seed = new byte[32];
        for (var i = 0; i < 32; i++)
        {
            seed[i] = (byte)(0x42 + i);
        }

        // Drive the seed through the same chain PrfService uses internally:
        // StoreKeysAsync derives the X25519 + Ed25519 keypair bundle from
        // the seed via HKDF and caches them under SyntheticKeyId.
        var storeResult = await _cryptoProvider.StoreKeysAsync(SyntheticKeyId, seed, ttlMs: null);
        if (!storeResult.Success || storeResult.Value is null)
        {
            return $"FAIL: StoreKeysAsync returned {storeResult.ErrorCode}";
        }

        // K = raw X25519 pubkey-bytes — same convention PrfVfsTest.razor uses
        // to seal slot 0 (the simple 1-receiver design where CK = pubkey).
        byte[] k;
        try
        {
            k = Convert.FromBase64String(storeResult.Value.X25519PublicKey);
        }
        catch (FormatException ex)
        {
            return $"FAIL: X25519 pubkey is not valid Base64: {ex.Message}";
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
                return $"FAIL: first install expected NO_EXISTING_DB, got {installA}";
            }

            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                await ctx.Database.EnsureCreatedAsync();
                for (var i = 0; i < RowCount; i++)
                {
                    ctx.Items.Add(new VfsTestItem
                    {
                        Marker = $"synthetic-{i}",
                        Payload = $"payload-{i}-{Guid.NewGuid():N}",
                    });
                }
                await ctx.SaveChangesAsync();
            }

            // Close + clear so the next install is a true reopen — the
            // worker must re-install K via the same code path the page
            // would on a fresh PRF ceremony.
            await _databaseService.CloseDatabaseAsync(dbName);
            await _databaseService.ClearEncryptionKeyAsync(dbName);

            // Phase 2 — reinstall K against the same on-disk DB. MATCH
            // proves slot 0 AEAD-authenticates under the synthetic-seed-
            // derived pubkey, i.e. the seed → keypair → K chain agrees
            // with what was sealed in phase 1.
            var installB = await _databaseService.InstallEncryptionKeyAsync(dbName, k);
            if (installB != VfsKeyInstallResult.MATCH)
            {
                return $"FAIL: reopen expected MATCH, got {installB}";
            }

            // Phase 3 — read rows back and verify the round-trip survived.
            List<VfsTestItem> rows;
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                rows = await ctx.Items.OrderBy(x => x.Id).ToListAsync();
            }

            if (rows.Count != RowCount)
            {
                return $"FAIL: expected {RowCount} rows after reopen, got {rows.Count}";
            }
            for (var i = 0; i < RowCount; i++)
            {
                if (rows[i].Marker != $"synthetic-{i}")
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
            // Drop the synthetic key from the JS cache + the encrypted DB
            // file so the demo page (which reuses PrfVfsTestContext.DatabaseName)
            // gets a clean OPFS on the next manual run.
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
