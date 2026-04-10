using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.CryptoSync;
using SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EncryptedDelta;

/// <summary>
/// Step-by-step encrypted delta integration tests.
/// Each test builds on the previous — seed → verify → export → import → verify.
/// Direct worker calls (no SyncOrchestrator) to validate the V2 crypto pipeline.
/// </summary>
internal class WorkerEncryptedRoundTripTest(
    IDbContextFactory<CryptoTestContext> cryptoFactory,
    ISqliteWasmDatabaseService databaseService)
    : CryptoSyncTestBase(cryptoFactory, databaseService)
{
    public override string Name => "CryptoSync_WorkerEncryptedRoundTrip";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Admin keys from the generated seed constants (deterministic from byte[32]{1..32}).
        // No BouncyCastle needed — all crypto happens in the worker via crypto-core.
        var adminX25519PrivateKey = CryptoTestContext.AdminX25519PrivateKey;
        var adminX25519PublicKey = CryptoTestContext.AdminX25519PublicKey;
        var adminEd25519PrivateKey = CryptoTestContext.AdminEd25519PrivateKey;
        var adminEd25519PublicKey = CryptoTestContext.AdminEd25519PublicKey;

        // ===== STEP 1: Verify admin seed is in open tables =====
        Console.WriteLine($"[{Name}] Step 1: Verify admin seed in open tables");

        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var admin = await ctx.Contacts.SingleOrDefaultAsync(c => c.IsAdmin);
            if (admin is null)
            {
                throw new InvalidOperationException("Admin contact not found in seed — SeedAdminBootstrap not applied?");
            }
            if (!admin.IsTrusted)
            {
                throw new InvalidOperationException("Admin contact is not trusted");
            }

            var group = await ctx.ShareGroups.SingleOrDefaultAsync(g =>
                g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
            if (group is null)
            {
                throw new InvalidOperationException("System ShareGroup not found in seed");
            }

            var target = await ctx.ShareTargets.SingleOrDefaultAsync(t =>
                t.ShareGroupId == group.Id && t.MemberPublicKey == admin.X25519PublicKey);
            if (target is null)
            {
                throw new InvalidOperationException("Admin ShareTarget not found in seed");
            }

            Console.WriteLine($"[{Name}] Step 1 OK: admin={admin.Username}, group={group.GroupContext}, target CEK={target.WrappedContentKey.Length}b");
        }

        // ===== STEP 2: Seed domain data + export as encrypted delta =====
        Console.WriteLine($"[{Name}] Step 2: Seed domain data + encrypted export");

        var item1Id = Guid.NewGuid();
        var item2Id = Guid.NewGuid();
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            ctx.CryptoTestItems.Add(new CryptoTestItem
            {
                Id = item1Id, Title = "Milk", Description = "2L", Price = 1.99m,
                IsBought = false, SharingScope = SharingScope.Public,
                SharingId = CryptoSyncBootstrap.SystemSharingId, UpdatedAt = DateTime.UtcNow
            });
            ctx.CryptoTestItems.Add(new CryptoTestItem
            {
                Id = item2Id, Title = "Eggs", Description = "12pk", Price = 3.49m,
                IsBought = true, SharingScope = SharingScope.Public,
                SharingId = CryptoSyncBootstrap.SystemSharingId, UpdatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        // Build V2CryptoHeader from seed data
        V2CryptoHeader v2Header;
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var group = await ctx.ShareGroups.SingleAsync(g =>
                g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
            var target = await ctx.ShareTargets.SingleAsync(t =>
                t.ShareGroupId == group.Id && t.MemberPublicKey == adminX25519PublicKey);

            v2Header = new V2CryptoHeader
            {
                Version = 2,
                SystemTables = ["Contacts", "ShareGroups", "ShareTargets", "Permissions"],
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

        // Export CryptoTestItems as encrypted delta
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

        if (shadowGroupBytes.Length == 0)
        {
            throw new InvalidOperationException("Encrypted export returned empty bytes");
        }

        Console.WriteLine($"[{Name}] Step 2 OK: exported {shadowGroupBytes.Length} bytes (ShadowRowGroup)");

        // ===== STEP 3: Import encrypted delta into fresh DB =====
        Console.WriteLine($"[{Name}] Step 3: Import encrypted delta + verify open table");

        // Rebuild header for import (keys were zeroed)
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var group = await ctx.ShareGroups.SingleAsync(g =>
                g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
            var target = await ctx.ShareTargets.SingleAsync(t =>
                t.ShareGroupId == group.Id && t.MemberPublicKey == adminX25519PublicKey);

            v2Header = new V2CryptoHeader
            {
                Version = 2,
                SystemTables = ["Contacts", "ShareGroups", "ShareTargets", "Permissions"],
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

        // Delete domain items from open table (simulating a fresh peer)
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            ctx.CryptoTestItems.RemoveRange(await ctx.CryptoTestItems.ToListAsync());
            await ctx.SaveChangesAsync();

            if (await ctx.CryptoTestItems.CountAsync() != 0)
            {
                throw new InvalidOperationException("Failed to delete items before import");
            }
        }

        // Import the encrypted delta
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

        // Deserialize ImportReport: [rowsImported, rowsSkipped, errors[]]
        var reportArray = MessagePackSerializer.Deserialize<object[]>(importReportBytes);
        var rowsImported = Convert.ToInt32(reportArray[0]);
        var rowsSkipped = Convert.ToInt32(reportArray[1]);

        Console.WriteLine($"[{Name}] Import: {rowsImported} imported, {rowsSkipped} skipped");

        if (rowsImported != 2)
        {
            throw new InvalidOperationException($"Expected 2 rows imported, got {rowsImported}");
        }
        if (rowsSkipped != 0)
        {
            throw new InvalidOperationException($"Expected 0 rows skipped, got {rowsSkipped}");
        }

        // Verify open table data integrity
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var items = await ctx.CryptoTestItems.OrderBy(i => i.Title).ToListAsync();
            if (items.Count != 2)
            {
                throw new InvalidOperationException($"Expected 2 items, got {items.Count}");
            }
            if (items[0].Title != "Eggs" || items[1].Title != "Milk")
            {
                throw new InvalidOperationException($"Titles: '{items[0].Title}', '{items[1].Title}'");
            }
            if (items[0].Price != 3.49m || items[1].Price != 1.99m)
            {
                throw new InvalidOperationException($"Prices: {items[0].Price}, {items[1].Price}");
            }
        }

        Console.WriteLine($"[{Name}] Step 3 OK: open table verified after import");
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
