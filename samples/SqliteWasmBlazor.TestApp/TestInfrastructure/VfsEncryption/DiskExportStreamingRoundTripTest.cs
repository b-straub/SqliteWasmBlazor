using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// End-to-end smoke for the streaming export path
/// (<see cref="IEncryptedSqliteWasmDatabaseService.ExportDiskToPubkeyAndDownloadAsync"/>).
/// Drives the production path: worker <c>exportDiskStream</c> handler +
/// bridge positional msgpack encoder + Blob composition + <c>&lt;a
/// download&gt;</c> click. The actual file save is a browser side-effect
/// the test doesn't verify — what it covers is the dispatch chain end-to-
/// end, which is enough to catch the regressions that have bitten this
/// path:
/// <list type="bullet">
///   <item>bridge sending <c>{type}</c> at top-level instead of nested
///         under <c>{data: {type}}</c> (worker destructure throws);</item>
///   <item>worker calling <c>withVfsKeyHeader</c> on a raw 32-byte K_wrap
///         (msgpackr unpack fails — "Data read, but end of buffer not
///         reached");</item>
///   <item>bridge encoder throwing on malformed metadata (missing
///         <c>prfSaltBase64</c>, wrong field order, etc.).</item>
/// </list>
/// Wire-format byte-correctness is verified separately by the byte[]
/// round-trip + streaming-import tests.
/// </summary>
internal sealed class DiskExportStreamingRoundTripTest
{
    private const int RowCount = 4;
    private const string CredId = "test-cred-streaming";

    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IEncryptedSqliteWasmDatabaseService _session;
    private readonly ICryptoProvider _cryptoProvider;
    private readonly ISecureKeyCache _keyCache;
    private readonly IPrfService _prfService;

    public string Name => "Disk_ExportStreaming_RoundTrip";

    public DiskExportStreamingRoundTripTest(
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

        var seed = new byte[32];
        for (var i = 0; i < 32; i++) { seed[i] = (byte)(0x42 + i); }
        var prfSeedCacheKey = $"prf-seed:{_prfService.Salt}";
        var jsKeyId = GetPrfJsKeyId();

        try
        {
            await PrimeKeyMaterialAsync(seed, prfSeedCacheKey);
            var ownPubkey = await _cryptoProvider.GetPublicKeysAsync(jsKeyId);
            if (!ownPubkey.Success || ownPubkey.Value is null)
            {
                return $"FAIL: GetPublicKeysAsync returned {ownPubkey.ErrorCode}";
            }

            var kVfs = await DeriveVfsKeyAsync();
            try
            {
                try { await _session.EnterEncryptedAsync(kVfs, CredId); }
                catch (Exception ex) { return $"FAIL[EnterEncrypted]: {ex.GetType().Name}: {ex.Message}"; }
                try { await PopulateAsync(); }
                catch (Exception ex) { return $"FAIL[Populate]: {ex.GetType().Name}: {ex.Message}"; }
            }
            finally { CryptographicOperations.ZeroMemory(kVfs); }

            // Production streaming export — drives the entire worker
            // dispatch + bridge encoder + Blob assembly + <a download>
            // click. Failure means the dispatch chain broke; success
            // means the chain produced an envelope and triggered the
            // browser download.
            try
            {
                await _session.ExportDiskToPubkeyAndDownloadAsync(
                    "test-streaming-export.eds",
                    ownPubkey.Value.X25519PublicKey,
                    CredId);
            }
            catch (Exception ex)
            {
                return $"FAIL[ExportStreaming]: {ex.GetType().Name}: {ex.Message}";
            }
            return "OK";
        }
        finally
        {
            _cryptoProvider.RemoveCachedKey(jsKeyId);
            _prfService.ClearKeys();
            await CleanupAsync(dbName);
            CryptographicOperations.ZeroMemory(seed);
        }
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
                Marker = $"stream-{i}",
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
