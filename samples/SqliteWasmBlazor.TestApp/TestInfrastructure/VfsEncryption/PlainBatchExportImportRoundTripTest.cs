using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Pure-plain round-trip via the new
/// <see cref="ISqliteWasmDatabaseService.ExportAllDatabasesAsync"/> +
/// <see cref="ISqliteWasmDatabaseService.ImportAllDatabasesAsync"/> ZIP
/// batch primitives. No encryption involved at any phase — exercises the
/// plain plane in isolation.
///
/// <para>
/// Validates: a Plain disk populated with rows can be exported as a ZIP
/// of native SQLite files, the pool can be wiped, and the ZIP imports
/// back into a fresh pool with all rows preserved. Single-DB here because
/// the test fixture only registers <c>PrfVfsTestContext</c> in its OPFS
/// pool — the same primitive iterates every DB the SAH pool holds, so
/// multi-DB pools work the same way.
/// </para>
/// </summary>
internal sealed class PlainBatchExportImportRoundTripTest
{
    private const int RowCount = 10;

    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IEncryptedSqliteWasmDatabaseService _session;

    public string Name => "Plain_BatchExportImport_RoundTrip";

    public PlainBatchExportImportRoundTripTest(
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
        await CleanupAsync();

        try
        {
            // Phase 1 — populate on a Plain disk.
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                await ctx.Database.EnsureCreatedAsync();
                for (var i = 0; i < RowCount; i++)
                {
                    ctx.Items.Add(new VfsTestItem
                    {
                        Marker = $"plainrt-{i}",
                        Payload = $"payload-{i}-{Guid.NewGuid():N}",
                    });
                }
                await ctx.SaveChangesAsync();
            }

            // Phase 2 — ZIP-export the whole pool.
            var zip = await _databaseService.ExportAllDatabasesAsync();
            if (zip.Length == 0)
            {
                return "FAIL: ExportAllDatabasesAsync returned empty ZIP";
            }

            // Phase 3 — wipe the pool.
            await _databaseService.DeleteDatabaseAsync(dbName);
            if (await _databaseService.ExistsDatabaseAsync(dbName))
            {
                return "FAIL: phase 3 expected DB deleted";
            }

            // Phase 4 — import the ZIP back; expect OK.
            var importOutcome = await _databaseService.ImportAllDatabasesAsync(zip);
            if (importOutcome != DiskImportResult.OK)
            {
                return $"FAIL: ImportAllDatabasesAsync expected OK, got {importOutcome}";
            }

            // Phase 5 — read rows back, verify intact.
            List<VfsTestItem> rows;
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                rows = await ctx.Items.OrderBy(x => x.Id).ToListAsync();
            }
            if (rows.Count != RowCount)
            {
                return $"FAIL: expected {RowCount} rows after ZIP round-trip, got {rows.Count}";
            }
            for (var i = 0; i < RowCount; i++)
            {
                if (rows[i].Marker != $"plainrt-{i}")
                {
                    return $"FAIL: row {i} Marker mismatch (got '{rows[i].Marker}')";
                }
            }

            return "OK";
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task CleanupAsync()
    {
        // Ensure the disk is Plain and the pool is empty before/after the
        // test — the new ZIP primitives only make sense on a Plain disk
        // (CanExecute returns false otherwise; the test would no-op).
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
