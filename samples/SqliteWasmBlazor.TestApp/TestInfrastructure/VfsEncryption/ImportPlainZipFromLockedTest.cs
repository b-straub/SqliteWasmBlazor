using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Plain-ZIP import onto an Encrypted+Locked disk. Verifies the recovery
/// path: when the user can't unlock (passkey unreachable), importing a
/// plain ZIP wipes the encrypted pool, drops the manifest + globalKey,
/// and lands the imported databases as plain — caller can re-encrypt
/// under any new passkey afterwards via EnterEncryptedAsync.
///
/// <para>
/// Sequence: build a Plain ZIP from a populated source DB → reset → enter
/// encrypted (empty pool) → lock → import the ZIP → assert Plain state →
/// read rows back from the post-import Plain disk.
/// </para>
/// </summary>
internal sealed class ImportPlainZipFromLockedTest
{
    private const int RowCount = 12;

    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IEncryptedSqliteWasmDatabaseService _session;

    public string Name => "ImportPlainZip_From_EncryptedLocked_EndsPlain";

    public ImportPlainZipFromLockedTest(
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
        for (var i = 0; i < 32; i++) { k[i] = (byte)(0x40 + i); }

        try
        {
            // Phase 1 — populate on Plain disk + capture ZIP.
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                await ctx.Database.EnsureCreatedAsync();
                for (var i = 0; i < RowCount; i++)
                {
                    ctx.Items.Add(new VfsTestItem
                    {
                        Marker = $"locked-{i}",
                        Payload = $"payload-{i}-{Guid.NewGuid():N}",
                    });
                }
                await ctx.SaveChangesAsync();
            }
            var zip = await _databaseService.ExportAllDatabasesAsync();
            if (zip.Length == 0)
            {
                return "FAIL: source ZIP empty";
            }

            // Phase 2 — EnterEncrypted on the populated Plain pool. This
            // encrypts the existing DB in place AND stamps the disk manifest
            // (manifest writing needs at least one DB to write into; empty-
            // pool EnterEncrypted leaves the manifest absent and state stays
            // Plain). Then LockAsync to drop globalKey while keeping the
            // manifest, landing the disk in Encrypted+Locked.
            await _session.EnterEncryptedAsync(k, "test-credential-id-import-locked");
            await _session.LockAsync();
            var preState = await _session.GetStateAsync();
            if (!preState.Encrypted || preState.Unlocked)
            {
                return $"FAIL: phase 2 expected Encrypted+Locked, got {preState}";
            }

            // Phase 3 — import the plain ZIP. Encrypted+Locked branch should
            // wipe pool + clear manifest + drop globalKey, then unpack.
            var importOutcome = await _session.ImportAllDatabasesAsync(zip);
            if (importOutcome != DiskImportResult.OK)
            {
                return $"FAIL: ImportAllDatabasesAsync expected OK, got {importOutcome}";
            }

            // Phase 4 — disk must report Plain after the wipe-and-import.
            var postState = await _session.GetStateAsync();
            if (postState.Encrypted)
            {
                return $"FAIL: phase 4 expected Plain, got {postState}";
            }

            // Phase 5 — read rows back via Plain DbContext.
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
                if (rows[i].Marker != $"locked-{i}")
                {
                    return $"FAIL: row {i} Marker mismatch (got '{rows[i].Marker}')";
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
