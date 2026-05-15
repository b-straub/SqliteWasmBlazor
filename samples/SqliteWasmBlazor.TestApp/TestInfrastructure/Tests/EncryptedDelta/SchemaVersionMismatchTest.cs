using System.Globalization;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Services;
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
    ISqliteWasmDatabaseService databaseService,
    IEncryptedSqliteWasmDatabaseService session)
    : CryptoSyncTestBase(cryptoFactory, databaseService, session)
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

        // Capture cursor before seeding so the delta envelope excludes the
        // system tables and carries only the CryptoTestItem we just wrote.
        var sinceCursor = DateTime.UtcNow;
        await Task.Delay(20);

        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            ctx.CryptoTestItems.Add(new CryptoTestItem
            {
                Id = Guid.NewGuid(), Title = "SchemaTest", Description = "test",
                Price = 1.00m, IsBought = false,
                SharingScope = SharingScope.PUBLIC,
                SharingId = CryptoSyncBootstrap.SystemSharingId, UpdatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        CryptoHeader cryptoHeader;
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var group = await ctx.ShareGroups.SingleAsync(g =>
                g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
            var target = await ctx.ShareTargets.SingleAsync(t =>
                t.ShareGroupId == group.Id && t.MemberPublicKey == CryptoTestContext.AdminX25519PublicKey);

            cryptoHeader = new CryptoHeader
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
                    WhereParams = [sinceCursor.ToString("O", CultureInfo.InvariantCulture)]
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

        Console.WriteLine($"[{Name}] Step 1 OK: exported {envelopeBytes.Length} bytes");

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
                t.ShareGroupId == group.Id && t.MemberPublicKey == CryptoTestContext.AdminX25519PublicKey);

            cryptoHeader = new CryptoHeader
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

        headerBytes = MessagePackSerializer.Serialize(cryptoHeader);
        try
        {
            await EncryptedSqliteWasmWorkerBridge.Instance.DeltaImportAsync(
                CryptoDatabaseName, headerBytes, envelopeBytes);

            // If we get here without error, the schema check didn't work
            throw new Exception("ASSERTION: Expected schema mismatch error, but import succeeded");
        }
        catch (Exception ex) when (ex.Message.StartsWith("ASSERTION:"))
        {
            cryptoHeader.Clear();
            throw new InvalidOperationException(ex.Message);
        }
        catch (Exception ex)
        {
            cryptoHeader.Clear();
            if (!ex.Message.Contains("schema mismatch"))
            {
                throw new InvalidOperationException(
                    $"Expected schema mismatch error, got: {ex.Message}");
            }
            Console.WriteLine($"[{Name}] Step 3 OK: schema mismatch correctly rejected");
        }

        return "OK";
    }
}
