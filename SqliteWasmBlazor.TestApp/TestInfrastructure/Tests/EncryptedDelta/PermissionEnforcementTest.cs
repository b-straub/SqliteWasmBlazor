using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.CryptoSync;
using SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EncryptedDelta;

/// <summary>
/// Comprehensive permission enforcement test covering all CRUD + column-level scenarios.
///
/// CryptoTestItem permissions (from attributes):
///   [Permissions("Editor", Delete = "Owner")]
///   [AllowUpdate("Viewer")] on IsBought
///
/// Resolved permissions:
///   Owner:  insert=✓ read=✓ update=✓ delete=✓
///   Editor: insert=✓ read=✓ update=✓ delete=✗
///   Viewer: insert=✗ read=✗ update=✗ delete=✗  readwrite=[IsBought]
///
/// Test steps:
///   A. Viewer insert denied (new row)
///   B. Viewer delete denied (tombstone)
///   C. Editor delete denied (tombstone)
///   D. Editor insert + update allowed
///   E. Viewer update denied (changes Price)
///   F. Viewer IsBought-only update allowed (readwrite override)
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

        // ===== STEP A+B: Viewer insert + delete denied =====
        Console.WriteLine($"[{Name}] Step A+B: Viewer insert + delete denied");
        {
            var itemId = Guid.NewGuid();
            var tombstoneId = Guid.NewGuid();
            await SeedItems(
                new CryptoTestItem
                {
                    Id = itemId, Title = "NewItem", Description = "insert test",
                    Price = 1.00m, IsBought = false
                },
                new CryptoTestItem
                {
                    Id = tombstoneId, Title = "Deleted", Description = "delete test",
                    Price = 2.00m, IsBought = false,
                    IsDeleted = true, DeletedAt = DateTime.UtcNow
                });

            var delta = await ExportDelta();
            await ClearOpenTable();
            await SetSenderRole(SyncRole.Viewer);

            var (imported, skipped, errors) = await ImportDelta(delta);

            AssertEqual(0, imported, "Viewer imported");
            AssertEqual(2, skipped, "Viewer skipped");
            AssertEqual(2, errors.Length, "Viewer errors");

            var openCount = await CountOpenTable();
            AssertEqual(0, openCount, "open table after Viewer insert+delete");

            // Denied rows must also leave NO shadow entry
            var shadowCount = await CountShadowTable();
            AssertEqual(0, shadowCount, "shadow table after Viewer denial");

            Console.WriteLine($"[{Name}] Step A+B OK: Viewer insert + delete denied (0 imported, 0 shadow)");
        }

        // ===== STEP C: Editor delete denied =====
        Console.WriteLine($"[{Name}] Step C: Editor delete denied");
        await ResetDatabase();
        {
            var tombstoneId = Guid.NewGuid();
            await SeedItems(
                new CryptoTestItem
                {
                    Id = tombstoneId, Title = "EditorDelete", Description = "should be denied",
                    Price = 3.00m, IsBought = false,
                    IsDeleted = true, DeletedAt = DateTime.UtcNow
                });

            var delta = await ExportDelta();
            await ClearOpenTable();
            await SetSenderRole(SyncRole.Editor);

            var (imported, skipped, errors) = await ImportDelta(delta);

            AssertEqual(0, imported, "Editor delete imported");
            AssertEqual(1, skipped, "Editor delete skipped");
            AssertEqual(1, errors.Length, "Editor delete errors");

            var shadowCount = await CountShadowTable();
            AssertEqual(0, shadowCount, "shadow table after Editor delete denial");

            Console.WriteLine($"[{Name}] Step C OK: Editor delete denied (0 shadow)");
        }

        // ===== STEP D: Editor insert + update allowed =====
        Console.WriteLine($"[{Name}] Step D: Editor insert + update allowed");
        await ResetDatabase();
        {
            var itemId = Guid.NewGuid();
            await SeedItems(
                new CryptoTestItem
                {
                    Id = itemId, Title = "EditorInsert", Description = "should work",
                    Price = 4.00m, IsBought = false
                });

            var delta = await ExportDelta();
            await ClearOpenTable();
            await SetSenderRole(SyncRole.Editor);

            var (imported, skipped, _) = await ImportDelta(delta);

            AssertEqual(1, imported, "Editor insert imported");
            AssertEqual(0, skipped, "Editor insert skipped");

            // Now update: modify Price, export, revert open table, import as Editor
            await using (var ctx = await CryptoFactory.CreateDbContextAsync())
            {
                var item = await ctx.CryptoTestItems.SingleAsync(i => i.Id == itemId);
                item.Price = 5.00m;
                item.UpdatedAt = DateTime.UtcNow;
                await ctx.SaveChangesAsync();
            }

            var updateDelta = await ExportDelta();

            // Revert so the worker sees a real update
            await using (var ctx = await CryptoFactory.CreateDbContextAsync())
            {
                var item = await ctx.CryptoTestItems.SingleAsync(i => i.Id == itemId);
                item.Price = 4.00m;
                await ctx.SaveChangesAsync();
            }

            var (updated, updateSkipped, _) = await ImportDelta(updateDelta);

            AssertEqual(1, updated, "Editor update imported");
            AssertEqual(0, updateSkipped, "Editor update skipped");

            // Verify the update actually applied
            await using (var ctx = await CryptoFactory.CreateDbContextAsync())
            {
                var item = await ctx.CryptoTestItems.SingleAsync(i => i.Id == itemId);
                AssertEqual(5.00m, item.Price, "Price after Editor update");
            }

            Console.WriteLine($"[{Name}] Step D OK: Editor insert + update allowed");
        }

        // ===== STEP E: Viewer update denied (changes Price) =====
        Console.WriteLine($"[{Name}] Step E: Viewer update denied (Price change)");
        await ResetDatabase();
        {
            var itemId = Guid.NewGuid();
            // First seed and import as Owner so the row exists
            await SeedItems(
                new CryptoTestItem
                {
                    Id = itemId, Title = "ViewerUpdate", Description = "exists",
                    Price = 6.00m, IsBought = false
                });
            var seedDelta = await ExportDelta();
            // Keep Owner role for seeding
            await SetSenderRole(SyncRole.Owner);
            await ImportDelta(seedDelta);

            // Now modify Price and re-export
            await using (var ctx = await CryptoFactory.CreateDbContextAsync())
            {
                var item = await ctx.CryptoTestItems.SingleAsync(i => i.Id == itemId);
                item.Price = 99.99m;
                item.UpdatedAt = DateTime.UtcNow;
                await ctx.SaveChangesAsync();
            }
            var updateDelta = await ExportDelta();

            // Revert open table to original Price so the worker sees a real change
            await using (var ctx = await CryptoFactory.CreateDbContextAsync())
            {
                var item = await ctx.CryptoTestItems.SingleAsync(i => i.Id == itemId);
                item.Price = 6.00m;
                await ctx.SaveChangesAsync();
            }

            // Switch to Viewer, import the update
            await SetSenderRole(SyncRole.Viewer);
            var (imported, skipped, errors) = await ImportDelta(updateDelta);

            AssertEqual(0, imported, "Viewer Price update imported");
            AssertEqual(1, skipped, "Viewer Price update skipped");

            // Verify Price unchanged
            await using (var ctx = await CryptoFactory.CreateDbContextAsync())
            {
                var item = await ctx.CryptoTestItems.SingleAsync(i => i.Id == itemId);
                AssertEqual(6.00m, item.Price, "Price after denied update");
            }

            Console.WriteLine($"[{Name}] Step E OK: Viewer update denied (Price unchanged)");
        }

        // ===== STEP F: Viewer IsBought-only update allowed (readwrite override) =====
        Console.WriteLine($"[{Name}] Step F: Viewer IsBought readwrite override");
        await ResetDatabase();
        {
            var itemId = Guid.NewGuid();
            // Seed as Owner
            await SeedItems(
                new CryptoTestItem
                {
                    Id = itemId, Title = "ViewerIsBought", Description = "override test",
                    Price = 7.00m, IsBought = false
                });
            var seedDelta = await ExportDelta();
            await SetSenderRole(SyncRole.Owner);
            await ImportDelta(seedDelta);

            // Modify ONLY IsBought (keep Price, Title, Description the same)
            await using (var ctx = await CryptoFactory.CreateDbContextAsync())
            {
                var item = await ctx.CryptoTestItems.SingleAsync(i => i.Id == itemId);
                item.IsBought = true;
                item.UpdatedAt = DateTime.UtcNow;
                await ctx.SaveChangesAsync();
            }
            var updateDelta = await ExportDelta();

            // Revert open table so the worker sees the actual change
            await using (var ctx = await CryptoFactory.CreateDbContextAsync())
            {
                var item = await ctx.CryptoTestItems.SingleAsync(i => i.Id == itemId);
                item.IsBought = false;
                await ctx.SaveChangesAsync();
            }

            // Switch to Viewer, import — should be allowed (IsBought is readwrite)
            await SetSenderRole(SyncRole.Viewer);
            var (imported, skipped, _) = await ImportDelta(updateDelta);

            AssertEqual(1, imported, "Viewer IsBought update imported");
            AssertEqual(0, skipped, "Viewer IsBought update skipped");

            // Verify IsBought changed
            await using (var ctx = await CryptoFactory.CreateDbContextAsync())
            {
                var item = await ctx.CryptoTestItems.SingleAsync(i => i.Id == itemId);
                AssertEqual(true, item.IsBought, "IsBought after readwrite update");
                AssertEqual(7.00m, item.Price, "Price unchanged after readwrite update");
            }

            Console.WriteLine($"[{Name}] Step F OK: Viewer IsBought readwrite override works");
        }

        Console.WriteLine($"[{Name}] All permission scenarios passed");
        return "OK";
    }

    // ================================================================
    // Helpers — reduce boilerplate across steps
    // ================================================================

    private async ValueTask SeedItems(params CryptoTestItem[] items)
    {
        await using var ctx = await CryptoFactory.CreateDbContextAsync();
        foreach (var item in items)
        {
            item.SharingScope = SharingScope.Public;
            item.SharingId = CryptoSyncBootstrap.SystemSharingId;
            if (item.UpdatedAt == default)
            {
                item.UpdatedAt = DateTime.UtcNow;
            }
            ctx.CryptoTestItems.Add(item);
        }
        await ctx.SaveChangesAsync();
    }

    private async ValueTask<byte[]> ExportDelta()
    {
        var v2Header = await BuildV2Header();
        var headerBytes = MessagePackSerializer.Serialize(v2Header);
        try
        {
            return await DatabaseService!.BulkExportEncryptedV2Async(
                CryptoDatabaseName, BuildExportMetadata("CryptoTestItems"), headerBytes);
        }
        finally
        {
            v2Header.Clear();
        }
    }

    private async ValueTask<(int Imported, int Skipped, object[] Errors)> ImportDelta(byte[] shadowGroupBytes)
    {
        var v2Header = await BuildV2Header();
        var headerBytes = MessagePackSerializer.Serialize(v2Header);
        byte[] reportBytes;
        try
        {
            reportBytes = await DatabaseService!.BulkImportEncryptedV2Async(
                CryptoDatabaseName, headerBytes, shadowGroupBytes);
        }
        finally
        {
            v2Header.Clear();
        }

        var report = MessagePackSerializer.Deserialize<object[]>(reportBytes);
        var imported = Convert.ToInt32(report[0]);
        var skipped = Convert.ToInt32(report[1]);
        var errors = report[2] as object[] ?? [];

        return (imported, skipped, errors);
    }

    private async ValueTask<V2CryptoHeader> BuildV2Header()
    {
        await using var ctx = await CryptoFactory.CreateDbContextAsync();
        var group = await ctx.ShareGroups.SingleAsync(g =>
            g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        var target = await ctx.ShareTargets.SingleAsync(t =>
            t.MemberPublicKey == CryptoTestContext.AdminX25519PublicKey);

        return new V2CryptoHeader
        {
            Version = 2,
            SystemTables = ["Contacts", "ShareGroups", "ShareTargets"],
            ClientContactId = (await ctx.Contacts.SingleAsync(c => c.IsAdmin)).Id,
            ClientX25519PrivateKey = Convert.FromBase64String(CryptoTestContext.AdminX25519PrivateKey),
            AdminX25519PublicKey = Convert.FromBase64String(group.AdminPublicKey),
            GroupContext = group.GroupContext,
            KeyVersion = group.KeyVersion,
            WrappedCek = target.WrappedContentKey,
            ClientEd25519PrivateKey = Convert.FromBase64String(CryptoTestContext.AdminEd25519PrivateKey),
            ClientEd25519PublicKey = Convert.FromBase64String(CryptoTestContext.AdminEd25519PublicKey)
        };
    }

    private async ValueTask SetSenderRole(SyncRole role)
    {
        await using var ctx = await CryptoFactory.CreateDbContextAsync();
        var target = await ctx.ShareTargets.SingleAsync(t =>
            t.MemberPublicKey == CryptoTestContext.AdminX25519PublicKey);
        target.Role = role;
        await ctx.SaveChangesAsync();
    }

    private async ValueTask ClearOpenTable()
    {
        await using var ctx = await CryptoFactory.CreateDbContextAsync();
        var items = await ctx.CryptoTestItems.IgnoreQueryFilters().ToListAsync();
        ctx.CryptoTestItems.RemoveRange(items);
        await ctx.SaveChangesAsync();
    }

    private async ValueTask<int> CountOpenTable()
    {
        await using var ctx = await CryptoFactory.CreateDbContextAsync();
        return await ctx.CryptoTestItems.IgnoreQueryFilters().CountAsync();
    }

    private async ValueTask<int> CountShadowTable()
    {
        // Query _crypto_CryptoTestItems directly via raw SQL
        await using var ctx = await CryptoFactory.CreateDbContextAsync();
        return await ctx.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM _crypto_CryptoTestItems").SingleAsync();
    }

    private async ValueTask ResetDatabase()
    {
        await using var ctx = await CryptoFactory.CreateDbContextAsync();
        await ctx.Database.EnsureDeletedAsync();
        await ctx.Database.EnsureCreatedAsync();
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
        }
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
