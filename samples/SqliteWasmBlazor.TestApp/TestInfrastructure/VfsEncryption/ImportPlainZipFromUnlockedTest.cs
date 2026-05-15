using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Plain-ZIP import onto an Encrypted+Unlocked disk. Verifies the
/// encryption-preserving path: each ZIP entry is re-encrypted on write
/// under the existing globalKey via the worker's importDbPlain handler.
/// Manifest, globalKey, and passkey binding all survive untouched.
///
/// <para>
/// Sequence: build a Plain ZIP from a populated source DB → reset → enter
/// encrypted (Unlocked, empty pool) → import the ZIP → assert
/// Encrypted+Unlocked state → read rows back → lock + unlock + read again
/// to prove the data is genuinely AEAD-protected on disk.
/// </para>
/// </summary>
internal sealed class ImportPlainZipFromUnlockedTest
{
    private const int RowCount = 12;

    private readonly IDbContextFactory<PrfVfsTestContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IEncryptedSqliteWasmDatabaseService _session;

    public string Name => "ImportPlainZip_From_EncryptedUnlocked_StaysEncrypted";

    public ImportPlainZipFromUnlockedTest(
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
        for (var i = 0; i < 32; i++) { k[i] = (byte)(0x60 + i); }

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
                        Marker = $"unlocked-{i}",
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

            // Phase 2 — EnterEncrypted on the populated Plain pool so the
            // manifest gets stamped (empty-pool EnterEncrypted leaves the
            // manifest absent and state would stay Plain). Lands in
            // Encrypted+Unlocked, ready for the plain-ZIP import path that
            // preserves encryption.
            await _session.EnterEncryptedAsync(k, "test-credential-id-import-unlocked");
            var preState = await _session.GetStateAsync();
            if (!preState.Encrypted || !preState.Unlocked)
            {
                return $"FAIL: phase 2 expected Encrypted+Unlocked, got {preState}";
            }

            // Phase 3 — import the plain ZIP. Encrypted+Unlocked branch
            // wipes DBs, then re-encrypts each entry under the registered
            // globalKey via importDbPlain. State must stay Encrypted+Unlocked.
            var importOutcome = await _session.ImportAllDatabasesAsync(zip);
            if (importOutcome != DiskImportResult.OK)
            {
                return $"FAIL: ImportAllDatabasesAsync expected OK, got {importOutcome}";
            }

            var postState = await _session.GetStateAsync();
            if (!postState.Encrypted || !postState.Unlocked)
            {
                return $"FAIL: phase 3 expected Encrypted+Unlocked post-import, got {postState}";
            }

            // Phase 4 — read rows back through the encrypted DbContext.
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
                if (rows[i].Marker != $"unlocked-{i}")
                {
                    return $"FAIL: row {i} Marker mismatch (got '{rows[i].Marker}')";
                }
            }

            // Phase 5 — Lock + Unlock cycle proves the on-disk bytes are
            // really AEAD-encrypted under k (not landed plain by accident).
            // A wrong-key Unlock would throw; a successful re-read confirms
            // the ZIP entries went through the rekeySlots encrypt path.
            await _session.LockAsync();
            await _session.UnlockAsync(k);
            await using (var ctx = await _factory.CreateDbContextAsync())
            {
                rows = await ctx.Items.OrderBy(x => x.Id).ToListAsync();
            }
            if (rows.Count != RowCount)
            {
                return $"FAIL: expected {RowCount} rows after lock+unlock, got {rows.Count}";
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
