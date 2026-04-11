using System.Globalization;
using BlazorPRF.Crypto.Abstractions.Services;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync;
using SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EncryptedDelta;

/// <summary>
/// End-to-end integration test covering:
///   1. <see cref="SharingService.ShareAsync"/> cascading a share assignment
///      from a <see cref="CryptoTestList"/> down to every
///      <see cref="CryptoTestListItem"/> via the FK walk.
///   2. <see cref="ISqliteWasmDatabaseService.BulkRotateKeyAsync"/> walking
///      every <c>_crypto_*</c> shadow table and re-encrypting rows whose
///      SharingId matches — so a parent-child group spanning multiple
///      tables rotates atomically in one call.
///
/// Flow:
///   • Seed List + 3 Items under the system SharingId.
///   • Create a fresh ShareGroup ("list-abc:v1") + admin ShareTarget.
///   • SharingService.ShareAsync reassigns the whole subtree to "list-abc".
///   • Export with a list-abc header → shadow rows for both tables are now
///     encrypted with the list-abc CEK and tagged with the list-abc SharingId.
///   • Create a "list-abc:v2" rotate target.
///   • BulkRotateKeyAsync(sharingId: "list-abc", newKeyVersion: 2) must
///     re-encrypt exactly 4 rows (1 List + 3 Items) — proving the walk.
/// </summary>
internal class SharingAndRotateTest(
    IDbContextFactory<CryptoTestContext> cryptoFactory,
    ISqliteWasmDatabaseService databaseService,
    IGroupEncryption groupEncryption)
    : CryptoSyncTestBase(cryptoFactory, databaseService)
{
    private readonly IGroupEncryption _groupEncryption = groupEncryption;

    public override string Name => "CryptoSync_SharingAndRotate";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        const string listAbcContext = "list-abc:v1";
        const string listAbcContextV2 = "list-abc:v2";
        const string listAbcSharingId = "list-abc";
        const int itemCount = 3;

        // ---------- Step 1: seed List + Items (system SharingId) ----------
        var listId = Guid.NewGuid();
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            ctx.CryptoTestLists.Add(new CryptoTestList
            {
                Id = listId,
                Name = "Shared Groceries",
                Description = "Integration test list",
                SharingScope = SharingScope.Public,
                SharingId = CryptoSyncBootstrap.SystemSharingId,
                UpdatedAt = DateTime.UtcNow
            });
            for (var i = 0; i < itemCount; i++)
            {
                ctx.CryptoTestListItems.Add(new CryptoTestListItem
                {
                    Id = Guid.NewGuid(),
                    ListId = listId,
                    ItemName = $"Item-{i}",
                    UnitPrice = 1.00m + i,
                    Quantity = i + 1,
                    SharingScope = SharingScope.Public,
                    SharingId = CryptoSyncBootstrap.SystemSharingId,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            await ctx.SaveChangesAsync();
        }

        // ---------- Step 2: create list-abc:v1 group + admin target ----------
        var adminPrivKey = Convert.FromBase64String(CryptoTestContext.AdminX25519PrivateKey);
        var adminPubKey = CryptoTestContext.AdminX25519PublicKey;
        var keysV1 = await _groupEncryption.CreateGroupKeysAsync(
            adminPrivKey, adminPubKey, [adminPubKey], listAbcContext);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(adminPrivKey);
        if (!keysV1.Success)
        {
            throw new InvalidOperationException($"CreateGroupKeys (v1) failed: {keysV1.ErrorCode}");
        }
        ShareGroup listAbcGroup;
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            listAbcGroup = new ShareGroup
            {
                Id = Guid.NewGuid(),
                GroupContext = listAbcContext,
                KeyVersion = 1,
                AdminPublicKey = adminPubKey,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SharingScope = SharingScope.Shared,
                SharingId = listAbcSharingId
            };
            ctx.ShareGroups.Add(listAbcGroup);

            var adminContact = await ctx.Contacts.SingleAsync(c => c.IsAdmin);
            ctx.ShareTargets.Add(new ShareTarget
            {
                Id = Guid.NewGuid(),
                ShareGroupId = listAbcGroup.Id,
                KeyVersion = 1,
                MemberPublicKey = adminPubKey,
                WrappedContentKey = CryptoSyncBootstrap.SerializeWrappedCek(keysV1.Value!.MemberKeys[0].WrappedContentKey),
                Role = SyncRole.Owner,
                GrantedByContactId = adminContact.Id,
                UpdatedAt = DateTime.UtcNow,
                SharingScope = SharingScope.Shared,
                SharingId = listAbcSharingId
            });
            await ctx.SaveChangesAsync();
        }

        // ---------- Step 3: SharingService.ShareAsync cascade ----------
        int reassigned;
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var sharing = new SharingService(ctx);
            reassigned = await sharing.ShareAsync("CryptoTestLists", listId, listAbcGroup);
        }
        if (reassigned != 1 + itemCount)
        {
            throw new InvalidOperationException(
                $"SharingService.ShareAsync expected to touch {1 + itemCount} rows (1 list + {itemCount} items), got {reassigned}");
        }

        // Verify open-table subtree moved
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var list = await ctx.CryptoTestLists.AsNoTracking().SingleAsync(l => l.Id == listId);
            if (list.SharingId != listAbcSharingId)
            {
                throw new InvalidOperationException($"List SharingId = {list.SharingId} (expected {listAbcSharingId})");
            }
            var items = await ctx.CryptoTestListItems.AsNoTracking().Where(i => i.ListId == listId).ToListAsync();
            foreach (var item in items)
            {
                if (item.SharingId != listAbcSharingId)
                {
                    throw new InvalidOperationException($"Item {item.Id} SharingId = {item.SharingId} (expected {listAbcSharingId})");
                }
            }
        }

        // ---------- Step 4: export with list-abc:v1 header ----------
        var sinceCursor = DateTime.UtcNow.AddMinutes(-1);
        var header = await BuildHeaderAsync(listAbcContext);
        var headerBytes = MessagePackSerializer.Serialize(header);
        try
        {
            var exportMetadata = new BulkExportMetadata
            {
                Mode = 1,
                Tables =
                [
                    new TableExportSpec
                    {
                        TableName = "CryptoTestLists",
                        IsSystemTable = false,
                        Where = "\"SharingId\" = ?",
                        WhereParams = [listAbcSharingId]
                    },
                    new TableExportSpec
                    {
                        TableName = "CryptoTestListItems",
                        IsSystemTable = false,
                        Where = "\"SharingId\" = ?",
                        WhereParams = [listAbcSharingId]
                    }
                ]
            };
            var envelopeBytes = await DatabaseService.BulkExportEncryptedV2Async(
                CryptoDatabaseName, exportMetadata, headerBytes);

            var envelope = MessagePackSerializer.Deserialize<DeltaEnvelope>(envelopeBytes);
            if (envelope.Groups.Count != 2)
            {
                throw new InvalidOperationException(
                    $"Expected 2 groups after share + export, got {envelope.Groups.Count}");
            }
            var totalRows = envelope.Groups.Sum(g => g.Rows.Count);
            if (totalRows != 1 + itemCount)
            {
                throw new InvalidOperationException(
                    $"Expected {1 + itemCount} shadow rows under {listAbcSharingId}, got {totalRows}");
            }
            Console.WriteLine($"[{Name}] Export OK: {totalRows} rows across {envelope.Groups.Count} groups under '{listAbcSharingId}'");
        }
        finally
        {
            header.Clear();
        }

        _ = sinceCursor.ToString("O", CultureInfo.InvariantCulture); // silences unused-var analyzer

        // ---------- Step 5: create list-abc:v2 rotate target ----------
        var adminPrivKey2 = Convert.FromBase64String(CryptoTestContext.AdminX25519PrivateKey);
        var keysV2 = await _groupEncryption.CreateGroupKeysAsync(
            adminPrivKey2, adminPubKey, [adminPubKey], listAbcContextV2);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(adminPrivKey2);
        if (!keysV2.Success)
        {
            throw new InvalidOperationException($"CreateGroupKeys (v2) failed: {keysV2.ErrorCode}");
        }
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var v2Group = new ShareGroup
            {
                Id = Guid.NewGuid(),
                GroupContext = listAbcContextV2,
                KeyVersion = 2,
                AdminPublicKey = adminPubKey,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SharingScope = SharingScope.Shared,
                SharingId = listAbcSharingId
            };
            ctx.ShareGroups.Add(v2Group);

            var adminContact = await ctx.Contacts.SingleAsync(c => c.IsAdmin);
            ctx.ShareTargets.Add(new ShareTarget
            {
                Id = Guid.NewGuid(),
                ShareGroupId = v2Group.Id,
                KeyVersion = 2,
                MemberPublicKey = adminPubKey,
                WrappedContentKey = CryptoSyncBootstrap.SerializeWrappedCek(keysV2.Value!.MemberKeys[0].WrappedContentKey),
                Role = SyncRole.Owner,
                GrantedByContactId = adminContact.Id,
                UpdatedAt = DateTime.UtcNow,
                SharingScope = SharingScope.Shared,
                SharingId = listAbcSharingId
            });
            await ctx.SaveChangesAsync();
        }

        // ---------- Step 6: rotate across ALL shadow tables in one call ----------
        var oldHeader = await BuildHeaderAsync(listAbcContext);
        var newHeader = await BuildHeaderAsync(listAbcContextV2);
        var oldHeaderBytes = MessagePackSerializer.Serialize(oldHeader);
        var newHeaderBytes = MessagePackSerializer.Serialize(newHeader);
        int rotatedCount;
        try
        {
            rotatedCount = await DatabaseService.BulkRotateKeyAsync(
                CryptoDatabaseName, oldHeaderBytes, newHeaderBytes,
                sharingId: listAbcSharingId,
                newKeyVersion: 2);
        }
        finally
        {
            oldHeader.Clear();
            newHeader.Clear();
        }

        if (rotatedCount != 1 + itemCount)
        {
            throw new InvalidOperationException(
                $"Expected rotation to affect {1 + itemCount} rows across both shadow tables, got {rotatedCount}");
        }

        // ---------- Step 7: verify per-table KeyVersion bumped in both shadows ----------
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var listShadowVersions = await ctx.Database
                .SqlQueryRaw<int>(
                    "SELECT KeyVersion AS Value FROM _crypto_CryptoTestLists WHERE SharingId = {0}",
                    listAbcSharingId)
                .ToListAsync();
            if (listShadowVersions.Count == 0)
            {
                throw new InvalidOperationException("No shadow rows for CryptoTestLists under list-abc after rotate");
            }
            if (listShadowVersions.Any(v => v != 2))
            {
                throw new InvalidOperationException(
                    $"CryptoTestLists shadow KeyVersion not bumped: {string.Join(',', listShadowVersions)}");
            }

            var itemShadowVersions = await ctx.Database
                .SqlQueryRaw<int>(
                    "SELECT KeyVersion AS Value FROM _crypto_CryptoTestListItems WHERE SharingId = {0}",
                    listAbcSharingId)
                .ToListAsync();
            if (itemShadowVersions.Count != itemCount)
            {
                throw new InvalidOperationException(
                    $"Expected {itemCount} shadow rows for CryptoTestListItems under list-abc, got {itemShadowVersions.Count}");
            }
            if (itemShadowVersions.Any(v => v != 2))
            {
                throw new InvalidOperationException(
                    $"CryptoTestListItems shadow KeyVersion not bumped: {string.Join(',', itemShadowVersions)}");
            }
        }

        Console.WriteLine($"[{Name}] Rotate OK: {rotatedCount} rows across 2 shadow tables re-keyed under '{listAbcSharingId}'");
        return "OK";
    }

    private async Task<V2CryptoHeader> BuildHeaderAsync(string groupContext)
    {
        await using var ctx = await CryptoFactory.CreateDbContextAsync();
        var group = await ctx.ShareGroups.SingleAsync(g => g.GroupContext == groupContext);
        var target = await ctx.ShareTargets.SingleAsync(t =>
            t.ShareGroupId == group.Id
            && t.MemberPublicKey == CryptoTestContext.AdminX25519PublicKey
            && t.KeyVersion == group.KeyVersion);
        var adminContact = await ctx.Contacts.SingleAsync(c => c.IsAdmin);

        return new V2CryptoHeader
        {
            Version = 2,
            SystemTables = ["Contacts", "ShareGroups", "ShareTargets"],
            ClientContactId = adminContact.Id,
            ClientX25519PrivateKey = Convert.FromBase64String(CryptoTestContext.AdminX25519PrivateKey),
            AdminX25519PublicKey = Convert.FromBase64String(group.AdminPublicKey),
            GroupContext = group.GroupContext,
            KeyVersion = group.KeyVersion,
            WrappedCek = target.WrappedContentKey,
            ClientEd25519PrivateKey = Convert.FromBase64String(CryptoTestContext.AdminEd25519PrivateKey),
            ClientEd25519PublicKey = Convert.FromBase64String(CryptoTestContext.AdminEd25519PublicKey)
        };
    }
}
