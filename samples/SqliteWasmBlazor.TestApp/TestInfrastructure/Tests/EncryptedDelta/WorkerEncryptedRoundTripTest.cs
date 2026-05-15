using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Services;
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
    ISqliteWasmDatabaseService databaseService,
    IEncryptedSqliteWasmDatabaseService session)
    : CryptoSyncTestBase(cryptoFactory, databaseService, session)
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

        // Capture the delta cursor BEFORE seeding so the export filters out
        // the already-seeded system-table rows (bootstrap Contacts/ShareGroups/
        // ShareTargets) and only carries the CryptoTestItems just written.
        var sinceCursor = DateTime.UtcNow;
        await Task.Delay(10); // ensure UpdatedAt timestamps are strictly > cursor

        var item1Id = Guid.NewGuid();
        var item2Id = Guid.NewGuid();
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            ctx.CryptoTestItems.Add(new CryptoTestItem
            {
                Id = item1Id, Title = "Milk", Description = "2L", Price = 1.99m,
                IsBought = false, SharingScope = SharingScope.PUBLIC,
                SharingId = CryptoSyncBootstrap.SystemSharingId, UpdatedAt = DateTime.UtcNow
            });
            ctx.CryptoTestItems.Add(new CryptoTestItem
            {
                Id = item2Id, Title = "Eggs", Description = "12pk", Price = 3.49m,
                IsBought = true, SharingScope = SharingScope.PUBLIC,
                SharingId = CryptoSyncBootstrap.SystemSharingId, UpdatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        // Build CryptoHeader from seed data
        CryptoHeader cryptoHeader;
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var group = await ctx.ShareGroups.SingleAsync(g =>
                g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
            var target = await ctx.ShareTargets.SingleAsync(t =>
                t.ShareGroupId == group.Id && t.MemberPublicKey == adminX25519PublicKey);

            cryptoHeader = new CryptoHeader
            {
                Version = 2,
                SystemTables = ["Contacts", "ShareGroups", "ShareTargets"],
                ClientContactId = (await ctx.Contacts.SingleAsync(c => c.IsAdmin)).Id,
                ClientX25519PrivateKey = adminX25519PrivateKey.AsSpan().ToArray(),
                AdminX25519PublicKey = Convert.FromBase64String(group.GroupAdminPublicKey),
                GroupContext = group.GroupContext,
                KeyVersion = group.KeyVersion,
                WrappedCek = target.WrappedContentKey,
                ClientEd25519PrivateKey = adminEd25519PrivateKey.AsSpan().ToArray(),
                ClientEd25519PublicKey = Convert.FromBase64String(adminEd25519PublicKey)
            };
        }

        // Export the delta: C# provides a single per-table spec with
        // UpdatedAt > sinceCursor, worker encrypts that one table and
        // returns a packed DeltaEnvelope with a single group.
        var exportMetadata = new BulkExportMetadata
        {
            Mode = 1,
            Tables =
            [
                new TableExportSpec
                {
                    TableName = "CryptoTestItems",
                    IsSystemTable = false,
                    Where = "\"UpdatedAt\" > ?",
                    WhereParams = [sinceCursor.ToString("O", System.Globalization.CultureInfo.InvariantCulture)]
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

        if (envelopeBytes.Length == 0)
        {
            throw new InvalidOperationException("Encrypted export returned empty bytes");
        }

        // Envelope sanity check: deserialize and assert the CryptoTestItems
        // group is present. The delta filter should have kept it out of any
        // other (system) tables.
        var envelope = MessagePackSerializer.Deserialize<DeltaEnvelope>(envelopeBytes);
        if (envelope.Groups.Count == 0)
        {
            throw new InvalidOperationException("Delta envelope has no groups — filter excluded CryptoTestItems?");
        }
        var itemsGroup = envelope.Groups.FirstOrDefault(g => g.TableName == "CryptoTestItems")
            ?? throw new InvalidOperationException("Delta envelope missing CryptoTestItems group");
        if (itemsGroup.Rows.Count != 2)
        {
            throw new InvalidOperationException($"Expected 2 rows in CryptoTestItems group, got {itemsGroup.Rows.Count}");
        }

        Console.WriteLine($"[{Name}] Step 2 OK: exported {envelopeBytes.Length} bytes (DeltaEnvelope, {envelope.Groups.Count} group(s))");

        // ===== STEP 3: Import encrypted delta into fresh DB =====
        Console.WriteLine($"[{Name}] Step 3: Import encrypted delta + verify open table");

        // Rebuild header for import (keys were zeroed)
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var group = await ctx.ShareGroups.SingleAsync(g =>
                g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
            var target = await ctx.ShareTargets.SingleAsync(t =>
                t.ShareGroupId == group.Id && t.MemberPublicKey == adminX25519PublicKey);

            cryptoHeader = new CryptoHeader
            {
                Version = 2,
                SystemTables = ["Contacts", "ShareGroups", "ShareTargets"],
                ClientContactId = (await ctx.Contacts.SingleAsync(c => c.IsAdmin)).Id,
                ClientX25519PrivateKey = adminX25519PrivateKey.AsSpan().ToArray(),
                AdminX25519PublicKey = Convert.FromBase64String(group.GroupAdminPublicKey),
                GroupContext = group.GroupContext,
                KeyVersion = group.KeyVersion,
                WrappedCek = target.WrappedContentKey,
                ClientEd25519PrivateKey = adminEd25519PrivateKey.AsSpan().ToArray(),
                ClientEd25519PublicKey = Convert.FromBase64String(adminEd25519PublicKey)
            };
        }

        // Delete domain items from open table (simulating a fresh peer)
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM CryptoTestItems");

            if (await ctx.CryptoTestItems.IgnoreQueryFilters().CountAsync() != 0)
            {
                throw new InvalidOperationException("Failed to delete items before import");
            }
        }

        // Import the delta envelope. Worker unpacks the envelope, verifies
        // the outer signature, staggers groups system-first, and applies
        // each per-group body.
        headerBytes = MessagePackSerializer.Serialize(cryptoHeader);
        byte[] importReportBytes;
        try
        {
            importReportBytes = await EncryptedSqliteWasmWorkerBridge.Instance.DeltaImportAsync(
                CryptoDatabaseName, headerBytes, envelopeBytes);
        }
        finally
        {
            cryptoHeader.Clear();
        }

        var report = MessagePackSerializer.Deserialize<ImportReport>(importReportBytes);
        var rowsImported = report.RowsImported;
        var rowsSkipped = report.RowsSkipped;

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
}
