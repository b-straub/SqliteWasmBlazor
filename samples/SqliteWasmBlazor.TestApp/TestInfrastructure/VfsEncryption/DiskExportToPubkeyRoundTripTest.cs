using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// End-to-end round-trip for the asymmetric disk-export flow:
/// <list type="number">
///   <item>Inject a synthetic 32-byte PRF seed into both the C# secure-key
///         cache (the slot <see cref="IPrfService.DeriveDomainKeyAsync"/>
///         reads from) and the JS keyCache (via <see cref="ICryptoProvider.StoreKeysAsync"/>),
///         simulating a successful WebAuthn ceremony without virtual-authenticator
///         scaffolding.</item>
///   <item>Encrypt the VFS under <c>K_VFS = HKDF(seed, "sqlite-vfs:globalKey:v1")</c>
///         and populate one DB.</item>
///   <item>Call <see cref="IEncryptedSqliteWasmDatabaseService.ExportDiskToPubkeyAsync"/>
///         with the seed-derived X25519 pubkey ("backup to self" path).
///         Sender ECIES-wraps a fresh K_wrap and rekeys the pool under it.</item>
///   <item>Reset the disk, re-inject the same seed (as if the user re-authed
///         with the same passkey on a fresh device), re-Encrypt under K_VFS.</item>
///   <item><see cref="IEncryptedSqliteWasmDatabaseService.ImportDiskAsync"/>
///         the v2 envelope. Recipient ECIES-unwraps K_wrap with the
///         seed-derived cached X25519 keyId; worker rekeys page ciphertext
///         from K_wrap to globalKey on the fly.</item>
///   <item>Verify rows survive the round-trip.</item>
/// </list>
///
/// <para>
/// The test exercises every cryptographic seam of the asymmetric path —
/// X25519-pub ECIES wrap, cached keyId unwrap, worker rekey-on-import,
/// disk-globalKey preservation. A failure here pinpoints which seam is
/// broken without the "user pasted the wrong key" ambiguity of the
/// browser-side encryption page.
/// </para>
/// </summary>
internal sealed class DiskExportToPubkeyRoundTripTest
{
    private const int RowCount = 8;

    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IEncryptedSqliteWasmDatabaseService _session;
    private readonly ICryptoProvider _cryptoProvider;
    private readonly ISecureKeyCache _keyCache;
    private readonly IPrfService _prfService;

    public string Name => "Disk_ExportToPubkey_AsymmetricRoundTrip";

    public DiskExportToPubkeyRoundTripTest(
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

        // Synthetic seed in place of WebAuthn-PRF output. Same seed used
        // on both "sender" and "recipient" halves — the production flow
        // backs up to the caller's own pubkey, so re-authing the same
        // passkey re-derives the matching private key.
        var seed = new byte[32];
        for (var i = 0; i < 32; i++)
        {
            seed[i] = (byte)(0x42 + i);
        }

        // Production PrfService stores the seed at this exact slot —
        // mirroring the convention so domain-key derivation on the
        // recipient side finds it without needing a real auth ceremony.
        var prfSeedCacheKey = $"prf-seed:{_prfService.Salt}";
        var jsKeyId = GetPrfJsKeyId();

        try
        {
            // ---- SENDER HALF -----------------------------------------------
            await PrimeKeyMaterialAsync(seed, prfSeedCacheKey);
            var ownPubkey = await _cryptoProvider.GetPublicKeysAsync(jsKeyId);
            if (!ownPubkey.Success || ownPubkey.Value is null)
            {
                return $"FAIL: GetPublicKeysAsync returned {ownPubkey.ErrorCode}";
            }

            // ---- ECIES SELF-TEST -------------------------------------------
            // Cross-check that ECIES round-trips a 32-byte payload under the
            // exact same cached keyId and public key pair the import path
            // will use later. If this self-test fails, the bug is in
            // ECIES itself (or in how priv/pub are derived). If it passes
            // but the full import still says "invalid tag", the K_wrap is
            // making it through ECIES intact and the bug is somewhere in
            // the page-rekey wire (envelope MessagePack, bridge, AAD).
            {
                byte[]? probeKey = new byte[32];
                byte[]? probeUnwrappedKey = null;
                try
                {
                    for (var i = 0; i < 32; i++) probeKey[i] = (byte)(0xC0 + i);

                    var probeWrap = await _cryptoProvider.EncryptAsymmetricFromBytesAsync(
                        probeKey,
                        ownPubkey.Value.X25519PublicKey);
                    if (!probeWrap.Success || probeWrap.Value is null)
                    {
                        return $"FAIL[ecies-self:wrap]: {probeWrap.ErrorCode}";
                    }

                    var probeUnwrap = await _prfService.DecryptAsymmetricToBytesAsync(probeWrap.Value);
                    if (!probeUnwrap.Success || probeUnwrap.Value is null)
                    {
                        return $"FAIL[ecies-self:unwrap]: {probeUnwrap.ErrorCode}";
                    }
                    probeUnwrappedKey = probeUnwrap.Value;

                    if (probeUnwrappedKey.Length != 32)
                    {
                        return $"FAIL[ecies-self:length]: unwrapped key is {probeUnwrappedKey.Length} bytes, expected 32";
                    }
                    for (var i = 0; i < 32; i++)
                    {
                        if (probeUnwrappedKey[i] != probeKey[i])
                        {
                            return $"FAIL[ecies-self:bytes]: mismatch at index {i} (sent 0x{probeKey[i]:X2}, got 0x{probeUnwrappedKey[i]:X2})";
                        }
                    }
                }
                finally
                {
                    if (probeKey is not null)
                    {
                        CryptographicOperations.ZeroMemory(probeKey);
                    }
                    if (probeUnwrappedKey is not null)
                    {
                        CryptographicOperations.ZeroMemory(probeUnwrappedKey);
                    }
                }
            }

            var kVfs = await DeriveVfsKeyAsync();
            try
            {
                try
                {
                    await _session.EnterEncryptedAsync(kVfs, "test-credential-id-asymmetric");
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
                CryptographicOperations.ZeroMemory(kVfs);
            }

            byte[] envelope;
            try
            {
                envelope = await _session.ExportDiskToPubkeyAsync(
                    ownPubkey.Value.X25519PublicKey,
                    "test-credential-id-asymmetric");
            }
            catch (Exception ex)
            {
                return $"FAIL: ExportDiskToPubkeyAsync threw {ex.GetType().Name}: {ex.Message}";
            }

            if (envelope.Length == 0)
            {
                return "FAIL: ExportDiskToPubkeyAsync returned empty envelope";
            }

            // ---- RECIPIENT HALF --------------------------------------------
            // Reset wipes both the disk AND the PRF cache (cascade through
            // ResetDiskAsync → PrfService.ClearKeys). Re-prime the seed
            // exactly as a fresh re-auth with the same passkey would.
            try
            {
                await _session.ResetDiskAsync();
            }
            catch (Exception ex)
            {
                return $"FAIL[recipient:Reset]: {ex.GetType().Name}: {ex.Message}";
            }
            await PrimeKeyMaterialAsync(seed, prfSeedCacheKey);

            var kVfsRecipient = await DeriveVfsKeyAsync();
            try
            {
                try
                {
                    await _session.EnterEncryptedAsync(kVfsRecipient, "test-credential-id-asymmetric");
                }
                catch (Exception ex)
                {
                    return $"FAIL[recipient:EnterEncrypted]: {ex.GetType().Name}: {ex.Message}";
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kVfsRecipient);
            }

            DiskImportResult result;
            try
            {
                result = await _session.ImportDiskAsync(envelope);
            }
            catch (Exception ex)
            {
                return $"FAIL: ImportDiskAsync threw {ex.GetType().Name}: {ex.Message}";
            }

            if (result != DiskImportResult.OK)
            {
                return $"FAIL: ImportDiskAsync returned {result}, expected OK";
            }

            // ---- VERIFY ----------------------------------------------------
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
                if (rows[i].Marker != $"asym-{i}")
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
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>
    /// Inject the synthetic seed into both the C# secure-key cache (under
    /// the production <c>prf-seed:{salt}</c> slot the PrfService reads from)
    /// and the JS-side keyCache (under the production <c>prf-keys:{salt}</c>
    /// keyId).
    /// Mirrors what <c>StoreSeedAndDeriveKeysAsync</c> does internally on
    /// a successful WebAuthn ceremony, minus the WebAuthn round-trip.
    /// </summary>
    private async Task PrimeKeyMaterialAsync(byte[] seed, string prfSeedCacheKey)
    {
        // ISecureKeyCache.Store copies into unmanaged memory — caller-owned
        // managed buffer is unchanged. Pass a fresh copy so any later
        // wipe of the test's `seed` field doesn't disturb the cache.
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

    /// <summary>
    /// Compute K_VFS = HKDF(seed, "sqlite-vfs:globalKey:v1") using the
    /// production <see cref="IPrfService.DeriveDomainKeyAsync"/> path so
    /// the test exercises the same derivation EncryptionModel.EnterEncrypted
    /// drives in the demo. Caller wipes the returned buffer in finally.
    /// </summary>
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
                Marker = $"asym-{i}",
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
