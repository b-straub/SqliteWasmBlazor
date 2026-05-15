using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Cross-key round-trip for the guided-import primitive
/// (<see cref="IEncryptedSqliteWasmDatabaseService.ImportDiskGuidedAsync"/>).
/// Distinct from the backup-to-self round-trip: sender + recipient use
/// DIFFERENT synthetic PRF seeds, simulating the A→B share flow where
/// user A exports an envelope targeting user B's pubkey and credentialId,
/// then user B picks the envelope and the guided primitive does
/// wipe + EnterEncrypted + rekey-import as one atomic operation.
/// <list type="number">
///   <item>Compute recipient B's X25519 pubkey from seed_B (no cache
///         mutation — pure derive).</item>
///   <item>Prime seed_A in both C# secure-key cache and JS keyCache;
///         EnterEncrypted under K_VFS_A; populate rows.</item>
///   <item>Export to (recipientPubB, "cred-B") — envelope's
///         <c>CredentialIdHint</c> stamps "cred-B".</item>
///   <item>Reset the disk (clears PRF). Prime seed_B at the canonical
///         keyId — recipient now has the keypair that can ECIES-unwrap
///         <c>K_wrap</c>.</item>
///   <item>Call <c>ImportDiskGuidedAsync(envelope, kVfsB, "cred-B")</c>.
///         The guided primitive validates the hint, wipes the pool
///         (without clearing PRF), EnterEncrypts under (kVfsB, "cred-B"),
///         then rekey-imports each envelope file.</item>
///   <item>Assert <c>state.Hint == "cred-B"</c> + rows survive.</item>
/// </list>
/// </summary>
internal sealed class DiskImportGuidedCrossKeyTest
{
    private const int RowCount = 8;
    private const string SenderCredentialId = "test-cred-A";
    private const string RecipientCredentialId = "test-cred-B";

    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IEncryptedSqliteWasmDatabaseService _session;
    private readonly ICryptoProvider _cryptoProvider;
    private readonly ISecureKeyCache _keyCache;
    private readonly IPrfService _prfService;

    public string Name => "Disk_ImportGuided_CrossKeyRoundTrip";

    public DiskImportGuidedCrossKeyTest(
        IDbContextFactory<PrfVfsTestContext> factory,
        ISqliteWasmDatabaseService databaseService,
        IEncryptedSqliteWasmDatabaseService session,
        ICryptoProvider cryptoProvider,
        ISecureKeyCache keyCache,
        IPrfService prfService)
    {
        _factory = factory;
        _databaseService = databaseService;
        _session = session;
        _cryptoProvider = cryptoProvider;
        _keyCache = keyCache;
        _prfService = prfService;
    }

    public async ValueTask<string?> RunAsync()
    {
        var dbName = PrfVfsTestContext.DatabaseName;
        await CleanupAsync(dbName);

        var seedA = MakeSeed(0x42);
        var seedB = MakeSeed(0x73);
        var prfSeedCacheKey = $"prf-seed:{_prfService.Salt}";

        try
        {
            // Compute recipient B's X25519 pubkey from seed_B WITHOUT
            // touching the live key caches. DeriveDualKeyPairAsync is a
            // pure derive that returns priv+pub; we only need the pub.
            // The recipient priv is rederived from cached seed_B during
            // the recipient half via ECIES unwrap (DecryptAsymmetricToBytesAsync).
            string recipientPubB;
            {
                var bKeys = await _cryptoProvider.DeriveDualKeyPairAsync(seedB);
                try
                {
                    recipientPubB = bKeys.X25519PublicKey;
                }
                finally
                {
                    bKeys.Clear();
                }
            }

            // ---- SENDER HALF (seed A) --------------------------------------
            await PrimeKeyMaterialAsync(seedA, prfSeedCacheKey);

            var kVfsA = await DeriveVfsKeyAsync();
            try
            {
                try
                {
                    await _session.EnterEncryptedAsync(kVfsA, SenderCredentialId);
                }
                catch (Exception ex)
                {
                    return $"FAIL[sender:EnterEncrypted]: {ex.GetType().Name}: {ex.Message}";
                }
                try
                {
                    await PopulateAsync();
                }
                catch (Exception ex)
                {
                    return $"FAIL[sender:Populate]: {ex.GetType().Name}: {ex.Message}";
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kVfsA);
            }

            byte[] envelope;
            try
            {
                envelope = await _session.ExportDiskToPubkeyAsync(
                    recipientPubB,
                    RecipientCredentialId);
            }
            catch (Exception ex)
            {
                return $"FAIL[sender:Export]: {ex.GetType().Name}: {ex.Message}";
            }
            if (envelope.Length == 0)
            {
                return "FAIL: ExportDiskToPubkeyAsync returned empty envelope";
            }

            // ---- RECIPIENT HALF (seed B, guided import) --------------------
            // Reset wipes the disk AND the PRF cache (which currently holds
            // seed_A). Prime seed_B at the canonical keyId — recipient now
            // has the keypair that ECIES-unwraps K_wrap. The guided primitive
            // takes over from here: it does wipe+EnterEncrypted+import
            // internally without clearing the PRF cache (so the unwrap can
            // run on the cached seed). We call ResetDisk first to clear
            // sender's state cleanly; the guided primitive's own wipe is a
            // no-op on the resulting empty pool.
            try
            {
                await _session.ResetDiskAsync();
            }
            catch (Exception ex)
            {
                return $"FAIL[recipient:Reset]: {ex.GetType().Name}: {ex.Message}";
            }
            await PrimeKeyMaterialAsync(seedB, prfSeedCacheKey);

            var kVfsB = await DeriveVfsKeyAsync();
            DiskImportResult result;
            try
            {
                try
                {
                    result = await _session.ImportDiskGuidedAsync(
                        envelope, kVfsB, RecipientCredentialId);
                }
                catch (Exception ex)
                {
                    return $"FAIL[recipient:GuidedImport]: {ex.GetType().Name}: {ex.Message}";
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kVfsB);
            }
            if (result != DiskImportResult.OK)
            {
                return $"FAIL: ImportDiskGuidedAsync returned {result}, expected OK";
            }

            // ---- VERIFY ----------------------------------------------------
            var state = await _session.GetStateAsync();
            if (!state.Encrypted || !state.Unlocked)
            {
                return $"FAIL: post-import state Encrypted={state.Encrypted} Unlocked={state.Unlocked}, expected Encrypted+Unlocked";
            }
            if (!string.Equals(state.Hint, RecipientCredentialId, StringComparison.Ordinal))
            {
                return $"FAIL: post-import disk hint '{state.Hint}', expected '{RecipientCredentialId}' (disk should be rebound to recipient's credentialId)";
            }

            List<VfsTestItem> rows;
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                rows = await ctx.Items.OrderBy(x => x.Id).ToListAsync();
            }
            if (rows.Count != RowCount)
            {
                return $"FAIL: expected {RowCount} rows after import, got {rows.Count}";
            }
            for (var i = 0; i < RowCount; i++)
            {
                if (rows[i].Marker != $"cross-{i}")
                {
                    return $"FAIL: row {i} Marker mismatch (got '{rows[i].Marker}')";
                }
            }

            return "OK";
        }
        finally
        {
            _cryptoProvider.RemoveCachedKey(GetPrfJsKeyId());
            _prfService.ClearKeys();
            await CleanupAsync(dbName);
            CryptographicOperations.ZeroMemory(seedA);
            CryptographicOperations.ZeroMemory(seedB);
        }
    }

    private static byte[] MakeSeed(byte basis)
    {
        var seed = new byte[32];
        for (var i = 0; i < 32; i++)
        {
            seed[i] = (byte)(basis + i);
        }
        return seed;
    }

    private async Task PrimeKeyMaterialAsync(byte[] seed, string prfSeedCacheKey)
    {
        var seedForCache = new byte[seed.Length];
        Array.Copy(seed, seedForCache, seed.Length);
        _keyCache.Store(prfSeedCacheKey, seedForCache);
        CryptographicOperations.ZeroMemory(seedForCache);

        var storeResult = await _cryptoProvider.StoreKeysAsync(GetPrfJsKeyId(), seed, ttlMs: null);
        if (!storeResult.Success || storeResult.Value is null)
        {
            throw new InvalidOperationException(
                $"PrimeKeyMaterialAsync: StoreKeysAsync failed ({storeResult.ErrorCode}).");
        }
    }

    private string GetPrfJsKeyId() => $"prf-keys:{_prfService.Salt}";

    private async Task<byte[]> DeriveVfsKeyAsync()
    {
        var derive = await _prfService.DeriveDomainKeyAsync(
            domainId: "vfs",
            context: "sqlite-vfs:globalKey:v1");
        if (!derive.Success || derive.Value is null)
        {
            throw new InvalidOperationException(
                $"DeriveVfsKeyAsync: DeriveDomainKeyAsync failed ({derive.ErrorCode}).");
        }
        var bytes = _keyCache.TryGet(derive.Value)
            ?? throw new InvalidOperationException("DeriveVfsKeyAsync: cache miss after derive.");
        if (bytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidOperationException(
                $"DeriveVfsKeyAsync: K_VFS must be 32 bytes, got {bytes.Length}.");
        }
        return bytes;
    }

    private async Task PopulateAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        await ctx.Database.EnsureCreatedAsync();
        for (var i = 0; i < RowCount; i++)
        {
            ctx.Items.Add(new VfsTestItem
            {
                Marker = $"cross-{i}",
                Payload = $"payload-{i}-{Guid.NewGuid():N}",
            });
        }
        await ctx.SaveChangesAsync();
    }

    private async Task CleanupAsync(string dbName)
    {
        try { await _session.ResetDiskAsync(); } catch { }
        try { await _databaseService.DeleteDatabaseAsync(dbName); } catch { }
    }
}
