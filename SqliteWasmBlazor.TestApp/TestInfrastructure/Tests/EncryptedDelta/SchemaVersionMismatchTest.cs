using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.CryptoSync;
using SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EncryptedDelta;

/// <summary>
/// Verifies that the worker rejects deltas from clients running a different
/// app version (different column registry = different migrations).
///
/// Flow:
///   1. Seed + export as normal
///   2. Modify _column_registry (add a fake column → hash changes)
///   3. Import — worker compares sender's schema hash vs local → mismatch → error
/// </summary>
internal class SchemaVersionMismatchTest(
    IDbContextFactory<CryptoTestContext> cryptoFactory,
    ISqliteWasmDatabaseService databaseService)
    : CryptoSyncTestBase(cryptoFactory, databaseService)
{
    public override string Name => "CryptoSync_SchemaVersionMismatch";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Step 1: Seed + export
        Console.WriteLine($"[{Name}] Step 1: Seed + export");

        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            ctx.CryptoTestItems.Add(new CryptoTestItem
            {
                Id = Guid.NewGuid(), Title = "SchemaTest", Description = "test",
                Price = 1.00m, IsBought = false,
                SharingScope = SharingScope.Public,
                SharingId = CryptoSyncBootstrap.SystemSharingId, UpdatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        V2CryptoHeader v2Header;
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var group = await ctx.ShareGroups.SingleAsync(g =>
                g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
            var target = await ctx.ShareTargets.SingleAsync(t =>
                t.MemberPublicKey == CryptoTestContext.AdminX25519PublicKey);

            v2Header = new V2CryptoHeader
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

        // Step 2: Tamper with _column_registry to simulate schema migration
        Console.WriteLine($"[{Name}] Step 2: Add fake column to _column_registry");

        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            // Add a fake column entry — changes the schema hash
            var fakeId = Guid.NewGuid().ToString();
            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO _column_registry (Id, TableName, ColumnIndex, ColumnName, SqlType, CSharpType, IsPrimaryKey) " +
                "VALUES ({0}, 'CryptoTestItems', 99, 'FakeNewColumn', 'TEXT', 'String', 0)", fakeId);
        }

        // Step 3: Import — should fail with schema mismatch
        Console.WriteLine($"[{Name}] Step 3: Import with mismatched schema");

        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var group = await ctx.ShareGroups.SingleAsync(g =>
                g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
            var target = await ctx.ShareTargets.SingleAsync(t =>
                t.MemberPublicKey == CryptoTestContext.AdminX25519PublicKey);

            v2Header = new V2CryptoHeader
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

        headerBytes = MessagePackSerializer.Serialize(v2Header);
        try
        {
            await DatabaseService.BulkImportEncryptedV2Async(
                CryptoDatabaseName, headerBytes, shadowGroupBytes);

            // If we get here without error, the schema check didn't work
            throw new Exception("ASSERTION: Expected schema mismatch error, but import succeeded");
        }
        catch (Exception ex) when (ex.Message.StartsWith("ASSERTION:"))
        {
            v2Header.Clear();
            throw new InvalidOperationException(ex.Message);
        }
        catch (Exception ex)
        {
            v2Header.Clear();
            if (!ex.Message.Contains("schema mismatch"))
            {
                throw new InvalidOperationException(
                    $"Expected schema mismatch error, got: {ex.Message}");
            }
            Console.WriteLine($"[{Name}] Step 3 OK: schema mismatch correctly rejected");
        }

        return "OK";
    }

    private static BulkExportMetadata BuildExportMetadata(string tableName)
    {
        var header = MessagePackFileHeaderV2.Create<CryptoTestItemDto>(
            tableName: tableName,
            primaryKeyColumn: "Id",
            recordCount: 0,
            mode: 1);

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
