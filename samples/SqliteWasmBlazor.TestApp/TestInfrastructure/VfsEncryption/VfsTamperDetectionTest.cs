using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Export the ciphertext of an encrypted DB, flip a byte inside a page's
/// ciphertext region, re-import via the opaque path, then reopen with the
/// correct key. Expect AEAD auth to reject the tampered slot — either as
/// a SQLite error during open/read, or as IOERR from the VFS.
/// </summary>
internal sealed class VfsTamperDetectionTest(
    IDbContextFactory<EncryptedTestContext> factory,
    ISqliteWasmDatabaseService databaseService,
    IEncryptedSqliteWasmDatabaseService session)
    : VfsEncryptionTestBase(factory, databaseService, session)
{
    public override string Name => "VFS_TamperDetection";

    public override async ValueTask<string?> RunTestAsync()
    {
        // Populate — more than one page's worth so we have a known slot 1 to tamper.
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            for (var i = 0; i < 200; i++)
            {
                ctx.Items.Add(new VfsTestItem
                {
                    Marker = $"tamper-src-{i}",
                    Payload = new string('X', 200),
                });
            }
            await ctx.SaveChangesAsync();
        }

        // Close worker handle — export below will re-close anyway, but be explicit.
        await DatabaseService.CloseDatabaseAsync(EncryptedDatabaseName);

        var ciphertext = await DatabaseService.ExportDatabaseAsync(EncryptedDatabaseName);
        // Physical slot is 4124 bytes (ciphertext 4096 + nonce 12 + tag 16).
        // We need at least two slots so we can tamper slot 1 cleanly.
        const int PhysicalSlotSize = 4124;
        const int PagePlaintextLen = 4096;
        if (ciphertext.Length < 2 * PhysicalSlotSize)
        {
            return $"Exported DB too small for tamper test: {ciphertext.Length}";
        }

        // Flip a byte inside slot 1's ciphertext body (bytes 0..4095 of that
        // slot; the nonce+tag tail lives at 4096..4123 of each 4124-byte slot).
        var tamperOffset = PhysicalSlotSize + 1000;
        if (tamperOffset >= PhysicalSlotSize + PagePlaintextLen)
        {
            return "tamperOffset would land in slot 1's nonce+tag tail — adjust";
        }
        ciphertext[tamperOffset] ^= 0xFF;

        // Re-import — the ciphertext bytes don't start with "SQLite format 3",
        // so ImportDatabaseAsync auto-detects them as opaque and skips both
        // the header check and the byte-18 WAL patch that would otherwise
        // invalidate the AEAD tag on slot 0.
        //
        // Opaque imports refuse-to-overwrite an existing DB, so we wipe
        // first. CloseDatabaseAsync above already cleared the key registry
        // for this path, so verify-on-write skips at import time — the
        // tamper detection we're testing surfaces at the next read instead,
        // which is exactly the path EF takes when reopening the DB below.
        await DatabaseService.DeleteDatabaseAsync(EncryptedDatabaseName);
        var importOutcome = await DatabaseService.ImportDatabaseAsync(EncryptedDatabaseName, ciphertext);
        if (importOutcome != DiskImportResult.OK)
        {
            return $"Expected import outcome OK (no key registered → no verify), got {importOutcome}";
        }

        // Reopen with the correct key. Reading any row should force the VFS to
        // decrypt slot 1 and fail.
        await using var ctx2 = await Factory.CreateDbContextAsync();
        try
        {
            var count = await ctx2.Items.CountAsync();
            // Table might be small enough that COUNT(*) only touches page 1.
            // If so, pull rows to force slot 1 into the page cache.
            _ = await ctx2.Items.ToListAsync();
            return $"FAIL: tampered slot 1 was readable; got {count} rows without AEAD failure";
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[{Name}] Tamper detected as expected: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            try { await DatabaseService.CloseDatabaseAsync(EncryptedDatabaseName); } catch { }
        }
    }
}
