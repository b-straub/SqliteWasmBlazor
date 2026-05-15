using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Encrypted-disk → plain round-trip via the canonical two-step path:
/// <list type="number">
///   <item><see cref="IEncryptedSqliteWasmDatabaseService.EnterEncryptedAsync"/>
///         + populate.</item>
///   <item><see cref="IEncryptedSqliteWasmDatabaseService.LeaveEncryptedAsync"/>
///         (in-place decrypt → Plain disk).</item>
///   <item><see cref="ISqliteWasmDatabaseService.ExportAllDatabasesAsync"/>
///         → ZIP of native .db files.</item>
///   <item>Wipe the pool.</item>
///   <item><see cref="ISqliteWasmDatabaseService.ImportAllDatabasesAsync"/>
///         (replace-all).</item>
///   <item>Verify rows.</item>
/// </list>
///
/// <para>
/// Replaces the prior single-call plain-export path. The encrypted interface
/// narrowed to envelope-only, so plain export of an encrypted disk is now expressed as
/// "leave encryption, then plain ZIP export" — each interface produces
/// its native format.
/// </para>
/// </summary>
internal sealed class DiskExportImportRoundTripTest
{
    private const int RowCount = 10;

    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IEncryptedSqliteWasmDatabaseService _session;

    public string Name => "Encrypted_ToPlain_ViaLeaveAndExportAll_RoundTrip";

    public DiskExportImportRoundTripTest(
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
        var dbName = PrfVfsTestContext.DatabaseName;
        await CleanupAsync(dbName);

        // Distinct deterministic byte pattern for the encryption key —
        // the test only needs encryption to be in place so we exercise
        // the EnterEncrypted → LeaveEncrypted → plain ZIP chain.
        var k = new byte[32];
        for (var i = 0; i < 32; i++) k[i] = (byte)(0x80 + i);

        try
        {
            // Phase 1 — encrypt + populate.
            await _session.EnterEncryptedAsync(k, "test-credential-id-encrypted-to-plain");
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                await ctx.Database.EnsureCreatedAsync();
                for (var i = 0; i < RowCount; i++)
                {
                    ctx.Items.Add(new VfsTestItem
                    {
                        Marker = $"e2p-{i}",
                        Payload = $"payload-{i}-{Guid.NewGuid():N}",
                    });
                }
                await ctx.SaveChangesAsync();
            }

            // Phase 2 — leave encrypted (in-place decrypt → Plain disk).
            await _session.LeaveEncryptedAsync();

            // Phase 3 — export the Plain disk as a ZIP of native .db files.
            var zip = await _databaseService.ExportAllDatabasesAsync();
            if (zip.Length == 0)
            {
                return "FAIL: ExportAllDatabasesAsync returned empty ZIP";
            }

            // Phase 4 — wipe the pool (delete every DB) so the import sees
            // a fresh pool and "rows came from the ZIP" is unambiguous.
            await _databaseService.DeleteDatabaseAsync(dbName);

            // Phase 5 — import the ZIP onto the Plain disk.
            // ImportAllDatabasesAsync wipes the pool first (no-op here)
            // and unpacks each ZIP entry as a fresh native .db.
            var importOutcome = await _databaseService.ImportAllDatabasesAsync(zip);
            if (importOutcome != DiskImportResult.OK)
            {
                return $"FAIL: ImportAllDatabasesAsync expected OK, got {importOutcome}";
            }

            // Phase 6 — read rows back from the Plain disk and verify.
            List<VfsTestItem> rows;
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                rows = await ctx.Items.OrderBy(x => x.Id).ToListAsync();
            }
            if (rows.Count != RowCount)
            {
                return $"FAIL: expected {RowCount} rows after ZIP import, got {rows.Count}";
            }
            for (var i = 0; i < RowCount; i++)
            {
                if (rows[i].Marker != $"e2p-{i}")
                {
                    return $"FAIL: row {i} Marker mismatch (got '{rows[i].Marker}')";
                }
            }

            return "OK";
        }
        finally
        {
            await CleanupAsync(dbName);
            CryptographicOperations.ZeroMemory(k);
        }
    }

    private async Task CleanupAsync(string dbName)
    {
        _ = dbName; // pool-wipe covers it; explicit name kept for callsite clarity
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
