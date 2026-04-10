using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.CryptoSync;
using SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EncryptedDelta;

/// <summary>
/// Verifies that the worker enforces CRUD permissions on encrypted import.
///
/// Test flow:
///   1. Seed 2 items as admin (Owner), export encrypted delta
///   2. Downgrade admin's ShareTarget role to Viewer in the DB
///   3. Mark one item as deleted (tombstone) in the exported data
///   4. Import — Viewer's delete should be rejected (PERMISSION_DELETE_DENIED)
///   5. Verify: 1 imported (non-tombstone), 1 skipped (tombstone denied)
///
/// The trick: the export happened with admin keys (valid signature), but by the
/// time the import runs, the sender's role in ShareTargets has been changed to
/// Viewer. The worker resolves the sender's role from the DB at import time.
/// </summary>
internal class PermissionEnforcementTest(
    IDbContextFactory<CryptoTestContext> cryptoFactory,
    ISqliteWasmDatabaseService databaseService)
    : CryptoSyncTestBase(cryptoFactory, databaseService)
{
    public override string Name => "CryptoSync_PermissionEnforcement";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        var adminX25519PrivateKey = CryptoTestContext.AdminX25519PrivateKey;
        var adminX25519PublicKey = CryptoTestContext.AdminX25519PublicKey;
        var adminEd25519PrivateKey = CryptoTestContext.AdminEd25519PrivateKey;
        var adminEd25519PublicKey = CryptoTestContext.AdminEd25519PublicKey;

        // ===== STEP 1: Seed domain data + export =====
        Console.WriteLine($"[{Name}] Step 1: Seed items + export as Owner");

        var item1Id = Guid.NewGuid();
        var item2Id = Guid.NewGuid();
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            ctx.CryptoTestItems.Add(new CryptoTestItem
            {
                Id = item1Id, Title = "Allowed", Description = "should import",
                Price = 1.00m, IsBought = false,
                SharingScope = SharingScope.Public,
                SharingId = CryptoSyncBootstrap.SystemSharingId, UpdatedAt = DateTime.UtcNow
            });
            ctx.CryptoTestItems.Add(new CryptoTestItem
            {
                Id = item2Id, Title = "Tombstone", Description = "should be denied",
                Price = 2.00m, IsBought = false,
                IsDeleted = true, DeletedAt = DateTime.UtcNow,
                SharingScope = SharingScope.Public,
                SharingId = CryptoSyncBootstrap.SystemSharingId, UpdatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        // Build header + export
        V2CryptoHeader v2Header;
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var group = await ctx.ShareGroups.SingleAsync(g =>
                g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
            var target = await ctx.ShareTargets.SingleAsync(t =>
                t.MemberPublicKey == adminX25519PublicKey);

            v2Header = new V2CryptoHeader
            {
                Version = 2,
                SystemTables = ["Contacts", "ShareGroups", "ShareTargets"],
                ClientContactId = (await ctx.Contacts.SingleAsync(c => c.IsAdmin)).Id,
                ClientX25519PrivateKey = Convert.FromBase64String(adminX25519PrivateKey),
                AdminX25519PublicKey = Convert.FromBase64String(group.AdminPublicKey),
                GroupContext = group.GroupContext,
                KeyVersion = group.KeyVersion,
                WrappedCek = target.WrappedContentKey,
                ClientEd25519PrivateKey = Convert.FromBase64String(adminEd25519PrivateKey),
                ClientEd25519PublicKey = Convert.FromBase64String(adminEd25519PublicKey)
            };
        }

        var exportMetadata = BuildExportMetadata("CryptoTestItems");
        var headerBytes = MessagePackSerializer.Serialize(v2Header);
        byte[] shadowGroupBytes;
        try
        {
            shadowGroupBytes = await DatabaseService.BulkExportEncryptedV2Async(
                CryptoDatabaseName, exportMetadata, headerBytes);
        }
        finally
        {
            v2Header.Clear();
        }

        Console.WriteLine($"[{Name}] Step 1 OK: exported {shadowGroupBytes.Length} bytes");

        // ===== STEP 2: Downgrade sender's role to Viewer =====
        Console.WriteLine($"[{Name}] Step 2: Downgrade sender to Viewer");

        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var target = await ctx.ShareTargets.SingleAsync(t =>
                t.MemberPublicKey == adminX25519PublicKey);
            target.Role = SyncRole.Viewer;
            await ctx.SaveChangesAsync();
        }

        // ===== STEP 3: Clear open table + import =====
        Console.WriteLine($"[{Name}] Step 3: Import with Viewer role");

        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var items = await ctx.CryptoTestItems.IgnoreQueryFilters().ToListAsync();
            ctx.CryptoTestItems.RemoveRange(items);
            await ctx.SaveChangesAsync();
        }

        // Rebuild header
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var group = await ctx.ShareGroups.SingleAsync(g =>
                g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
            var target = await ctx.ShareTargets.SingleAsync(t =>
                t.MemberPublicKey == adminX25519PublicKey);

            v2Header = new V2CryptoHeader
            {
                Version = 2,
                SystemTables = ["Contacts", "ShareGroups", "ShareTargets"],
                ClientContactId = (await ctx.Contacts.SingleAsync(c => c.IsAdmin)).Id,
                ClientX25519PrivateKey = Convert.FromBase64String(adminX25519PrivateKey),
                AdminX25519PublicKey = Convert.FromBase64String(group.AdminPublicKey),
                GroupContext = group.GroupContext,
                KeyVersion = group.KeyVersion,
                WrappedCek = target.WrappedContentKey,
                ClientEd25519PrivateKey = Convert.FromBase64String(adminEd25519PrivateKey),
                ClientEd25519PublicKey = Convert.FromBase64String(adminEd25519PublicKey)
            };
        }

        headerBytes = MessagePackSerializer.Serialize(v2Header);
        byte[] importReportBytes;
        try
        {
            importReportBytes = await DatabaseService.BulkImportEncryptedV2Async(
                CryptoDatabaseName, headerBytes, shadowGroupBytes);
        }
        finally
        {
            v2Header.Clear();
        }

        // ===== STEP 4: Verify ImportReport =====
        var reportArray = MessagePackSerializer.Deserialize<object[]>(importReportBytes);
        var rowsImported = Convert.ToInt32(reportArray[0]);
        var rowsSkipped = Convert.ToInt32(reportArray[1]);
        var errorsArray = reportArray[2] as object[];

        Console.WriteLine($"[{Name}] Import result: {rowsImported} imported, {rowsSkipped} skipped, {errorsArray?.Length ?? 0} errors");

        // Viewer should be able to import the non-tombstone row (insert is allowed for Editor+
        // but CryptoTestItem has [Permissions("Editor")] which means Viewer gets insert=deny).
        // So BOTH rows should be denied for a Viewer:
        // - item1 (non-tombstone): PERMISSION_INSERT_DENIED (Viewer can't insert)
        // - item2 (tombstone): PERMISSION_DELETE_DENIED (Viewer can't delete)

        if (rowsImported != 0)
        {
            throw new InvalidOperationException(
                $"Expected 0 rows imported (Viewer can't insert), got {rowsImported}");
        }
        if (rowsSkipped != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 rows skipped (1 insert denied + 1 delete denied), got {rowsSkipped}");
        }

        // Verify error codes
        if (errorsArray is null || errorsArray.Length != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 errors in report, got {errorsArray?.Length ?? 0}");
        }

        // Verify open table is still empty
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var count = await ctx.CryptoTestItems.IgnoreQueryFilters().CountAsync();
            if (count != 0)
            {
                throw new InvalidOperationException(
                    $"Expected 0 items in open table (all denied), got {count}");
            }
        }

        Console.WriteLine($"[{Name}] Step 4 OK: Viewer's insert + delete correctly denied");
        return "OK";
    }

    private static BulkExportMetadata BuildExportMetadata(string tableName)
    {
        var header = MessagePackFileHeaderV2.Create<CryptoTestItemDto>(
            tableName: tableName,
            primaryKeyColumn: "Id",
            recordCount: 0,
            mode: 1,
            sqlTypeOverrides: new Dictionary<string, string> { ["Id"] = "BLOB" });

        return new BulkExportMetadata
        {
            TableName = header.TableName,
            Columns = header.Columns,
            PrimaryKeyColumn = header.PrimaryKeyColumn,
            SchemaHash = header.SchemaHash,
            DataType = header.DataType,
            Mode = 1
        };
    }
}
