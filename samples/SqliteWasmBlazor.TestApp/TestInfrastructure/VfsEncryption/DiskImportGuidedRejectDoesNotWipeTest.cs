using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Regression for guided .eds import ordering. A mistargeted envelope must
/// fail unwrap/preflight before the primitive wipes the current Plain/Locked
/// disk.
/// </summary>
internal sealed class DiskImportGuidedRejectDoesNotWipeTest
{
    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IEncryptedSqliteWasmDatabaseService _session;
    private readonly ICryptoProvider _cryptoProvider;
    private readonly ISecureKeyCache _keyCache;
    private readonly IPrfService _prfService;

    public string Name => "Disk_ImportGuided_RejectDoesNotWipePlainDisk";

    public DiskImportGuidedRejectDoesNotWipeTest(
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

        var seedA = MakeSeed(0x22);
        var seedB = MakeSeed(0x44);
        var seedC = MakeSeed(0x66);
        var prfSeedCacheKey = $"prf-seed:{_prfService.Salt}";

        try
        {
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

            await PrimeKeyMaterialAsync(seedA, prfSeedCacheKey);
            var kVfsA = await DeriveVfsKeyAsync();
            byte[] envelope;
            try
            {
                await _session.EnterEncryptedAsync(kVfsA, "guided-reject-sender");
                await PopulateAsync("sender");
                envelope = await _session.ExportDiskToPubkeyAsync(
                    recipientPubB,
                    "guided-reject-recipient");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kVfsA);
            }

            await _session.ResetDiskAsync();
            await PopulateAsync("victim");

            await PrimeKeyMaterialAsync(seedC, prfSeedCacheKey);
            var kVfsC = await DeriveVfsKeyAsync();
            try
            {
                try
                {
                    var result = await _session.ImportDiskGuidedAsync(
                        envelope,
                        kVfsC,
                        "guided-reject-recipient");
                    if (result == DiskImportResult.OK)
                    {
                        return "FAIL: mistargeted guided import unexpectedly succeeded";
                    }
                }
                catch (InvalidOperationException)
                {
                    // Expected: ECIES unwrap fails before any destructive wipe.
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kVfsC);
            }

            var state = await _session.GetStateAsync();
            if (state.Encrypted)
            {
                return $"FAIL: rejected guided import changed Plain disk state to {state}";
            }

            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                var rows = await ctx.Items.OrderBy(x => x.Id).ToListAsync();
                if (rows.Count != 1 || rows[0].Marker != "victim")
                {
                    return $"FAIL: rejected guided import wiped or changed rows; count={rows.Count}";
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
            CryptographicOperations.ZeroMemory(seedC);
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

    private async Task PopulateAsync(string marker)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        await ctx.Database.EnsureCreatedAsync();
        ctx.Items.Add(new VfsTestItem
        {
            Marker = marker,
            Payload = $"payload-{Guid.NewGuid():N}",
        });
        await ctx.SaveChangesAsync();
    }

    private async Task CleanupAsync(string dbName)
    {
        try { await _session.ResetDiskAsync(); } catch { }
        try { await _databaseService.DeleteDatabaseAsync(dbName); } catch { }
    }
}
