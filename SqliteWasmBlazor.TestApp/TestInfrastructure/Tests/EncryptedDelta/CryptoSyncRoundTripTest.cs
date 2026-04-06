using BlazorPRF.Crypto.Abstractions;
using BlazorPRF.Crypto.Abstractions.Models;
using BlazorPRF.Crypto.Testing;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.CryptoSync;
using SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EncryptedDelta;

/// <summary>
/// Full encrypted delta roundtrip: seed data → SyncOrchestrator.Export → delete → Import → verify.
/// Uses BouncyCastle crypto (works in WASM), real worker for BulkExport/Import.
/// </summary>
internal class CryptoSyncRoundTripTest(
    IDbContextFactory<CryptoTestContext> cryptoFactory,
    ISqliteWasmDatabaseService databaseService)
    : CryptoSyncTestBase(cryptoFactory, databaseService)
{
    public override string Name => "CryptoSync_RoundTrip";

    public override async ValueTask<string?> RunTestAsync()
    {
        var crypto = new BouncyCastleCryptoProvider();

        // 1. Derive keys for Alice (owner) and Bob (editor)
        var aliceSeed = new byte[32];
        Random.Shared.NextBytes(aliceSeed);
        var bobSeed = new byte[32];
        Random.Shared.NextBytes(bobSeed);

        var aliceKeys = await crypto.DeriveDualKeyPairAsync(aliceSeed);
        var bobKeys = await crypto.DeriveDualKeyPairAsync(bobSeed);

        // 2. Set up contacts
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var contactService = new ContactService(ctx, crypto);
            await contactService.AddContactAsync(
                new ContactUserData { Username = "Alice", Email = "alice@test.com" },
                aliceKeys.X25519PublicKey, aliceKeys.Ed25519PublicKey,
                SyncRole.Owner, TrustLevel.Full, TrustDirection.Sent, aliceSeed);
            await contactService.AddContactAsync(
                new ContactUserData { Username = "Bob", Email = "bob@test.com" },
                bobKeys.X25519PublicKey, bobKeys.Ed25519PublicKey,
                SyncRole.Editor, TrustLevel.Full, TrustDirection.Sent, aliceSeed);
        }

        // 3. Seed test items via EF Core
        var item1Id = Guid.NewGuid();
        var item2Id = Guid.NewGuid();
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            ctx.CryptoTestItems.Add(new CryptoTestItem
            {
                Id = item1Id,
                Title = "Milk",
                Description = "2 liters",
                Price = 1.99m,
                IsBought = false,
                SharingScope = SharingScope.Public,
                SharingId = "list-1",
                UpdatedAt = DateTime.UtcNow
            });
            ctx.CryptoTestItems.Add(new CryptoTestItem
            {
                Id = item2Id,
                Title = "Eggs",
                Description = "12 pack",
                Price = 3.49m,
                IsBought = false,
                SharingScope = SharingScope.Public,
                SharingId = "list-1",
                UpdatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        // 4. Export via SyncOrchestrator
        var exportMetadata = BuildExportMetadata("CryptoTestItems");

        byte[] envelopeBytes;
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var contactService = new ContactService(ctx, crypto);
            var permissionService = new PermissionService(ctx);
            var orchestrator = new SyncOrchestrator(DatabaseService!, crypto, contactService, permissionService);

            envelopeBytes = await orchestrator.ExportAsync(
                CryptoDatabaseName, exportMetadata, aliceKeys, aliceKeys);
        }

        if (envelopeBytes.Length == 0)
        {
            throw new InvalidOperationException("Export returned empty bytes");
        }

        Console.WriteLine($"[{Name}] Encrypted envelope: {envelopeBytes.Length} bytes");

        // 5. Delete all items
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var items = await ctx.CryptoTestItems.ToListAsync();
            ctx.CryptoTestItems.RemoveRange(items);
            await ctx.SaveChangesAsync();

            if (await ctx.CryptoTestItems.CountAsync() != 0)
            {
                throw new InvalidOperationException("Failed to delete items");
            }
        }

        // 6. Import as Bob via SyncOrchestrator
        int rowsImported;
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var contactService = new ContactService(ctx, crypto);
            var permissionService = new PermissionService(ctx);
            var orchestrator = new SyncOrchestrator(DatabaseService!, crypto, contactService, permissionService);

            var allColumns = new Dictionary<string, string[]>
            {
                ["CryptoTestItems"] = ["Title", "Description", "Price", "IsBought"]
            };

            rowsImported = await orchestrator.ImportAsync(
                CryptoDatabaseName, envelopeBytes, bobKeys, allColumns);
        }

        if (rowsImported != 2)
        {
            throw new InvalidOperationException($"Expected 2 rows imported, got {rowsImported}");
        }

        // 7. Verify data roundtrip
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var items = await ctx.CryptoTestItems.OrderBy(i => i.Title).ToListAsync();

            if (items.Count != 2)
            {
                throw new InvalidOperationException($"Expected 2 items, got {items.Count}");
            }

            if (items[0].Title != "Eggs" || items[1].Title != "Milk")
            {
                throw new InvalidOperationException($"Titles don't match: '{items[0].Title}', '{items[1].Title}'");
            }

            if (items[0].Price != 3.49m || items[1].Price != 1.99m)
            {
                throw new InvalidOperationException($"Prices don't match: {items[0].Price}, {items[1].Price}");
            }
        }

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
