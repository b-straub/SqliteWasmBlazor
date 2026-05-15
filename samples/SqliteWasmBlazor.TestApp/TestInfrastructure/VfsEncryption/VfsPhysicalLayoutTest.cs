using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Confirms the PRF-VFS offset-remap invariant: every 4096-byte logical page
/// that SQLite writes expands to 4124 bytes on disk
/// (ciphertext 4096 + nonce 12 + tag 16). After materializing several pages
/// via multi-row inserts, the exported raw OPFS bytes must:
///
/// 1. Have a length that is an exact multiple of the 4124-byte physical slot
///    size (SAHPool's 4096-byte per-file header is stripped by
///    <c>ExportDatabaseAsync</c>).
/// 2. Span at least two slots (pages) so we exercise boundary math.
/// 3. Support a full read-back — if the logical→physical translation in
///    xFileSize / xTruncate were off by even one byte, SQLite would miscount
///    pages and either short-read or interpret adjacent-slot ciphertext as
///    page data, and SELECT would fail.
/// </summary>
internal sealed class VfsPhysicalLayoutTest(
    IDbContextFactory<EncryptedTestContext> factory,
    ISqliteWasmDatabaseService databaseService,
    IEncryptedSqliteWasmDatabaseService session)
    : VfsEncryptionTestBase(factory, databaseService, session)
{
    public override string Name => "VFS_PhysicalLayout";

    private const int PhysicalSlotSize = 4124;

    public override async ValueTask<string?> RunTestAsync()
    {
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            // Enough rows at ~400B payload each to spill onto multiple pages.
            for (var i = 0; i < 500; i++)
            {
                ctx.Items.Add(new VfsTestItem
                {
                    Marker = $"r{i}",
                    Payload = new string('p', 400),
                });
            }
            await ctx.SaveChangesAsync();
        }

        var bytes = await DatabaseService.ExportDatabaseAsync(EncryptedDatabaseName);

        if (bytes.Length == 0)
        {
            return "Exported zero bytes — DB not materialized";
        }
        if (bytes.Length % PhysicalSlotSize != 0)
        {
            return
                $"Exported length {bytes.Length} is not a multiple of physical slot size {PhysicalSlotSize} — " +
                $"logical→physical remap broken (remainder: {bytes.Length % PhysicalSlotSize})";
        }

        var slots = bytes.Length / PhysicalSlotSize;
        if (slots < 2)
        {
            return $"Expected at least 2 physical slots, got {slots} — multi-page boundary not exercised";
        }

        // Read-back across many pages validates xFileSize/xTruncate
        // translation is correct end-to-end.
        int rowCount;
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            rowCount = await ctx.Items.CountAsync();
        }
        if (rowCount != 500)
        {
            return $"Expected 500 rows, found {rowCount} — multi-page read-back broken";
        }

        Console.WriteLine(
            $"[{Name}] {bytes.Length} bytes = {slots} * {PhysicalSlotSize}, 500 rows readable — slot invariant holds");
        return null;
    }
}
