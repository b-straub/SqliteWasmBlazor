using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Regression for encrypted-unlocked plain ZIP import preflight. A ZIP entry
/// with SQLite magic but a non-4096-byte physical shape used to pass C#
/// preflight, wipe the encrypted pool, and fail only inside worker rekeySlots.
/// </summary>
internal sealed class ImportPlainZipRejectsBadShapeTest
{
    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IEncryptedSqliteWasmDatabaseService _session;

    public string Name => "ImportPlainZip_BadShape_DoesNotWipeUnlockedDisk";

    public ImportPlainZipRejectsBadShapeTest(
        IDbContextFactory<PrfVfsTestContext> factory,
        ISqliteWasmDatabaseService databaseService,
        IEncryptedSqliteWasmDatabaseService session)
    {
        _factory = factory;
        _databaseService = databaseService;
        _session = session;
    }

    public async ValueTask<string?> RunAsync()
    {
        await CleanupAsync();

        var k = new byte[32];
        for (var i = 0; i < 32; i++) { k[i] = (byte)(0x90 + i); }

        try
        {
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                await ctx.Database.EnsureCreatedAsync();
                ctx.Items.Add(new VfsTestItem
                {
                    Marker = "shape-survivor",
                    Payload = $"payload-{Guid.NewGuid():N}",
                });
                await ctx.SaveChangesAsync();
            }

            await _session.EnterEncryptedAsync(k, "test-credential-id-import-bad-shape");

            var badZip = BuildBadShapeZip();
            var outcome = await _session.ImportAllDatabasesAsync(badZip);
            if (outcome != DiskImportResult.WRONG_KEY)
            {
                return $"FAIL: expected WRONG_KEY for bad-shape ZIP, got {outcome}";
            }

            var state = await _session.GetStateAsync();
            if (!state.Encrypted || !state.Unlocked)
            {
                return $"FAIL: rejected ZIP changed disk state to {state}";
            }

            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                var rows = await ctx.Items.OrderBy(x => x.Id).ToListAsync();
                if (rows.Count != 1 || rows[0].Marker != "shape-survivor")
                {
                    return $"FAIL: rejected ZIP wiped or changed rows; count={rows.Count}";
                }
            }

            return "OK";
        }
        finally
        {
            await CleanupAsync();
            CryptographicOperations.ZeroMemory(k);
        }
    }

    private static byte[] BuildBadShapeZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(PrfVfsTestContext.DatabaseName);
            using var stream = entry.Open();
            stream.Write("SQLite format 3\0"u8);
            stream.WriteByte(0x42);
        }
        return ms.ToArray();
    }

    private async Task CleanupAsync()
    {
        try { await _session.ResetDiskAsync(); } catch { }
        try
        {
            var names = await _databaseService.ListDatabasesAsync();
            foreach (var n in names)
            {
                try { await _databaseService.DeleteDatabaseAsync(n); } catch { }
            }
        }
        catch { }
    }
}
