using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Abstractions;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// R3.1 composition test: drive a synthetic 32-byte PRF seed through the
/// production key-derivation chain (StoreKeysAsync → X25519 keypair →
/// pubkey-bytes K → worker global-key install → encrypted VFS), write rows,
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
    private readonly IEncryptedSqliteWasmDatabaseService _session;

    public string Name => "Synthetic_PrfSeed_DrivesEncryptedVfsRoundTrip";

    public SyntheticPrfSeedRoundTripTest(
        IDbContextFactory<PrfVfsTestContext> factory,
        ISqliteWasmDatabaseService databaseService,
        ICryptoProvider cryptoProvider,
        IEncryptedSqliteWasmDatabaseService session)
    {
        _factory = factory;
        _databaseService = databaseService;
        _cryptoProvider = cryptoProvider;
        _session = session;
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
            // Phase 1 — install K as the worker-wide global key, then
            // materialise an encrypted DB and write rows. (Single-key
            // model: SetEncryptionKeyAsync is idempotent and applies to
            // every DB opened while it is set.)
            if (await _databaseService.ExistsDatabaseAsync(dbName))
            {
                return $"FAIL: first install expected fresh DB, but {dbName} already exists";
            }
            await _session.UnlockAsync(k);

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

            // Phase 2 — reinstall K and read rows back. SetEncryptionKey
            // implicitly closes the open DB and swaps globalKey, so the
            // next read forces a true reopen (the worker re-stamps file.key
            // from globalKey at xOpen — the same path a fresh PRF
            // ceremony would drive). A successful read proves slot 0
            // AEAD-authenticates under the synthetic-seed-derived pubkey.
            await _session.UnlockAsync(k);

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
            CryptographicOperations.ZeroMemory(k);
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private async Task CleanupAsync(string dbName)
    {
        // ClearEncryptionKey implicitly closes every open DB before
        // dropping globalKey; one call covers both close-and-clear.
        try { await _session.ResetDiskAsync(); } catch { }
        try { await _databaseService.DeleteDatabaseAsync(dbName); } catch { }
    }
}
