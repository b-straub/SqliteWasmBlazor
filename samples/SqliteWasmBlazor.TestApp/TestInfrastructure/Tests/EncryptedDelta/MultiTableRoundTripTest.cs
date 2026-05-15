using System.Globalization;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.CryptoSync;
using SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EncryptedDelta;

/// <summary>
/// Multi-table round-trip: seed a <see cref="CryptoTestList"/> plus a handful
/// of <see cref="CryptoTestListItem"/>s under it, export one delta envelope
/// covering both tables (parent-before-children via spec ordering), clear,
/// re-import, and verify both tables rehydrate with their FK link intact.
///
/// This is the multi-table companion to <c>WorkerEncryptedRoundTripTest</c>
/// — same keys, same system group, same filter pattern, but now with two
/// domain tables in a single envelope.
/// </summary>
internal class MultiTableRoundTripTest(
    IDbContextFactory<CryptoTestContext> cryptoFactory,
    ISqliteWasmDatabaseService databaseService,
    IEncryptedSqliteWasmDatabaseService session)
    : CryptoSyncTestBase(cryptoFactory, databaseService, session)
{
    public override string Name => "CryptoSync_MultiTableRoundTrip";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // ---------- Arrange ----------
        const int itemCount = 5;
        var sinceCursor = DateTime.UtcNow;
        await Task.Delay(20);

        var listId = Guid.NewGuid();
        var expectedItemIds = new List<Guid>();

        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            ctx.CryptoTestLists.Add(new CryptoTestList
            {
                Id = listId,
                Name = "Groceries",
                Description = "Round-trip test list",
                SharingScope = SharingScope.PUBLIC,
                SharingId = CryptoSyncBootstrap.SystemSharingId,
                UpdatedAt = DateTime.UtcNow
            });

            for (var i = 0; i < itemCount; i++)
            {
                var itemId = Guid.NewGuid();
                expectedItemIds.Add(itemId);
                ctx.CryptoTestListItems.Add(new CryptoTestListItem
                {
                    Id = itemId,
                    ListId = listId,
                    ItemName = $"Item-{i}",
                    UnitPrice = 1.00m + i,
                    Quantity = i + 1,
                    SharingScope = SharingScope.PUBLIC,
                    SharingId = CryptoSyncBootstrap.SystemSharingId,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            await ctx.SaveChangesAsync();
        }

        // ---------- Build header ----------
        CryptoHeader cryptoHeader;
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            cryptoHeader = await BuildHeaderAsync(ctx);
        }

        // ---------- Export delta (two-table spec list) ----------
        var sinceIso = sinceCursor.ToString("O", CultureInfo.InvariantCulture);
        var exportMetadata = new BulkExportMetadata
        {
            Mode = 1,
            Tables =
            [
                // Parent first so the importer can re-insert before children —
                // FK is RESTRICT, not cascade, so the order matters here even
                // though the shadow/open apply path is DeltaWins.
                new TableExportSpec
                {
                    TableName = "CryptoTestLists",
                    IsSystemTable = false,
                    Where = "\"UpdatedAt\" > ?",
                    WhereParams = [sinceIso]
                },
                new TableExportSpec
                {
                    TableName = "CryptoTestListItems",
                    IsSystemTable = false,
                    Where = "\"UpdatedAt\" > ?",
                    WhereParams = [sinceIso]
                }
            ]
        };

        var headerBytes = MessagePackSerializer.Serialize(cryptoHeader);
        byte[] envelopeBytes;
        try
        {
            envelopeBytes = await EncryptedSqliteWasmWorkerBridge.Instance.DeltaExportAsync(
                CryptoDatabaseName, exportMetadata, headerBytes);
        }
        finally
        {
            cryptoHeader.Clear();
        }

        var envelope = MessagePackSerializer.Deserialize<DeltaEnvelope>(envelopeBytes);
        if (envelope.Groups.Count != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 groups in envelope (List + Items), got {envelope.Groups.Count}");
        }

        var listsGroup = envelope.Groups.SingleOrDefault(g => g.TableName == "CryptoTestLists")
            ?? throw new InvalidOperationException("Envelope missing CryptoTestLists group");
        var itemsGroup = envelope.Groups.SingleOrDefault(g => g.TableName == "CryptoTestListItems")
            ?? throw new InvalidOperationException("Envelope missing CryptoTestListItems group");

        if (listsGroup.Rows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 row in CryptoTestLists group, got {listsGroup.Rows.Count}");
        }
        if (itemsGroup.Rows.Count != itemCount)
        {
            throw new InvalidOperationException(
                $"Expected {itemCount} rows in CryptoTestListItems group, got {itemsGroup.Rows.Count}");
        }

        Console.WriteLine($"[{Name}] Export OK: {envelopeBytes.Length} bytes, {envelope.Groups.Count} groups " +
            $"(Lists={listsGroup.Rows.Count}, Items={itemsGroup.Rows.Count})");

        // ---------- Clear open + shadow (children first because FK RESTRICT) ----------
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM CryptoTestListItems");
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM CryptoTestLists");
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM _crypto_CryptoTestListItems");
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM _crypto_CryptoTestLists");

            if (await ctx.CryptoTestLists.IgnoreQueryFilters().CountAsync() != 0
                || await ctx.CryptoTestListItems.IgnoreQueryFilters().CountAsync() != 0)
            {
                throw new InvalidOperationException("Failed to clear both tables before import");
            }
        }

        // ---------- Import the envelope ----------
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            cryptoHeader = await BuildHeaderAsync(ctx);
        }
        headerBytes = MessagePackSerializer.Serialize(cryptoHeader);

        byte[] reportBytes;
        try
        {
            reportBytes = await EncryptedSqliteWasmWorkerBridge.Instance.DeltaImportAsync(
                CryptoDatabaseName, headerBytes, envelopeBytes);
        }
        finally
        {
            cryptoHeader.Clear();
        }

        var report = MessagePackSerializer.Deserialize<ImportReport>(reportBytes);
        var expectedTotal = 1 + itemCount;
        if (report.RowsImported != expectedTotal)
        {
            var err = report.Errors.Count > 0
                ? $"; first error = {report.Errors[0].Code} on {report.Errors[0].Table}:{report.Errors[0].RowId}"
                : "";
            throw new InvalidOperationException(
                $"Expected {expectedTotal} rows imported, got {report.RowsImported} (skipped {report.RowsSkipped}){err}");
        }
        if (report.Errors.Count != 0)
        {
            throw new InvalidOperationException(
                $"Unexpected import errors: first = {report.Errors[0].Code} — {report.Errors[0].Message}");
        }

        // ---------- Verify open tables + FK link ----------
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var restoredList = await ctx.CryptoTestLists
                .Include(l => l.Items)
                .SingleOrDefaultAsync(l => l.Id == listId)
                ?? throw new InvalidOperationException($"Parent list {listId} missing after import");

            if (restoredList.Name != "Groceries")
            {
                throw new InvalidOperationException($"List.Name corrupted: '{restoredList.Name}'");
            }
            if (restoredList.Items.Count != itemCount)
            {
                throw new InvalidOperationException(
                    $"Expected {itemCount} items under list, got {restoredList.Items.Count}");
            }

            var restoredIds = restoredList.Items.Select(i => i.Id).ToHashSet();
            foreach (var expectedId in expectedItemIds)
            {
                if (!restoredIds.Contains(expectedId))
                {
                    throw new InvalidOperationException($"Missing expected item {expectedId}");
                }
            }

            foreach (var item in restoredList.Items)
            {
                if (item.ListId != listId)
                {
                    throw new InvalidOperationException(
                        $"Item {item.Id} has wrong ListId: {item.ListId} (expected {listId})");
                }
            }
        }

        Console.WriteLine($"[{Name}] Import OK: {report.RowsImported} rows across 2 tables, FK preserved");
        return "OK";
    }

    private static async Task<CryptoHeader> BuildHeaderAsync(CryptoTestContext ctx)
    {
        var group = await ctx.ShareGroups.SingleAsync(g =>
            g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        var target = await ctx.ShareTargets.SingleAsync(t =>
            t.ShareGroupId == group.Id && t.MemberPublicKey == CryptoTestContext.AdminX25519PublicKey);

        return new CryptoHeader
        {
            Version = 2,
            SystemTables = ["Contacts", "ShareGroups", "ShareTargets"],
            ClientContactId = (await ctx.Contacts.SingleAsync(c => c.IsAdmin)).Id,
            ClientX25519PrivateKey = CryptoTestContext.AdminX25519PrivateKey.AsSpan().ToArray(),
            AdminX25519PublicKey = Convert.FromBase64String(group.GroupAdminPublicKey),
            GroupContext = group.GroupContext,
            KeyVersion = group.KeyVersion,
            WrappedCek = target.WrappedContentKey,
            ClientEd25519PrivateKey = CryptoTestContext.AdminEd25519PrivateKey.AsSpan().ToArray(),
            ClientEd25519PublicKey = Convert.FromBase64String(CryptoTestContext.AdminEd25519PublicKey)
        };
    }
}
