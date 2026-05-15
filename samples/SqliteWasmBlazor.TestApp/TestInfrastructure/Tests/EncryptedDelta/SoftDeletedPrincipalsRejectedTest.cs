using System.Globalization;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.CryptoSync;
using SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EncryptedDelta;

/// <summary>
/// F-002 regression — the worker's raw-SQL authorization lookups MUST
/// filter <c>IsDeleted = 0</c>. EF <c>HasQueryFilter</c> is enforced only on
/// EF queries; <c>db.exec()</c> in the worker sees soft-deleted rows
/// regardless and historically authorized them. The fix added the SQL
/// filter to <c>resolveSenderPermissions</c> Step 1 (Contacts) and Step 2a
/// (ShareTargets ⋈ ShareGroups join).
///
/// <para>This test covers the two load-bearing additions: the ShareTarget
/// IsDeleted filter and the ShareGroup IsDeleted filter on the join. The
/// Contacts IsDeleted filter on Step 1 is harder to single out as a
/// regression because <c>verifyGroupAdminIsTrusted</c> already filtered
/// <c>IsDeleted = 0</c> pre-fix, so a soft-deleted admin Contact would
/// reject either way (defense-in-depth, not novel rejection capability).</para>
///
/// <para>Both sub-cases route a domain-table mutation (CryptoTestItems) so
/// the import path goes through <c>resolveSenderPermissions</c> rather than
/// the system-table admin gate. Pre-fix the join returned the soft-deleted
/// row → permission chain validates as OWNER → row imports. Post-fix the
/// join filters to 0 rows → returns null → PERMISSION_SENDER_UNAUTHORIZED.</para>
/// </summary>
internal class SoftDeletedPrincipalsRejectedTest(
    IDbContextFactory<CryptoTestContext> cryptoFactory,
    ISqliteWasmDatabaseService databaseService,
    IEncryptedSqliteWasmDatabaseService session)
    : CryptoSyncTestBase(cryptoFactory, databaseService, session)
{
    public override string Name => "CryptoSync_SoftDeletedPrincipalsRejected";

    private DateTime _sinceCursor = DateTime.MinValue;

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        await RunSubCaseAsync(
            label: "soft-deleted ShareTarget",
            applyTamper: async () =>
            {
                await using var ctx = await CryptoFactory.CreateDbContextAsync();
                await ctx.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE ShareTargets SET IsDeleted = 1 WHERE MemberPublicKey = {CryptoTestContext.AdminX25519PublicKey}");
            });

        await ResetDatabaseAsync();

        await RunSubCaseAsync(
            label: "soft-deleted ShareGroup",
            applyTamper: async () =>
            {
                await using var ctx = await CryptoFactory.CreateDbContextAsync();
                await ctx.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE ShareGroups SET IsDeleted = 1 WHERE GroupContext = {CryptoSyncBootstrap.SystemGroupContext}");
            });

        Console.WriteLine($"[{Name}] All soft-deleted-principal sub-cases rejected");
        return "OK";
    }

    private async ValueTask RunSubCaseAsync(string label, Func<ValueTask> applyTamper)
    {
        Console.WriteLine($"[{Name}] Step: {label}");

        // Seed a fresh CryptoTestItem; capture the cursor before the insert
        // so ExportDelta only picks up this row.
        var itemId = Guid.NewGuid();
        _sinceCursor = DateTime.UtcNow;
        await Task.Delay(20);

        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            ctx.CryptoTestItems.Add(new CryptoTestItem
            {
                Id = itemId,
                Title = $"F-002 {label}",
                Description = "should be rejected",
                Price = 1.00m,
                IsBought = false,
                UpdatedAt = DateTime.UtcNow,
                SharingScope = SharingScope.PUBLIC,
                SharingId = CryptoSyncBootstrap.SystemSharingId
            });
            await ctx.SaveChangesAsync();
        }

        // Build the export envelope while every principal is still live —
        // the export side reads ShareTargets/ShareGroups via EF to derive
        // the CEK; soft-deleting them first would either fail-fast or skip
        // the row via HasQueryFilter.
        var delta = await ExportItemsDeltaAsync();

        // Build the import header while ShareTarget/ShareGroup are still
        // live. After this point the receiver-side worker query reads via
        // raw SQL, and that path is what the IsDeleted filter must catch.
        var headerBytes = await BuildHeaderBytesAsync();

        // Wipe open table so the import sees a fresh insert (not a stale
        // row colliding via PK).
        await ClearItemsOpenTableAsync();

        // Tamper: flip IsDeleted on the principal under test.
        await applyTamper();

        var report = await ImportWithHeaderAsync(headerBytes, delta);

        AssertEqual(0, report.RowsImported, $"Imported rows after {label}");
        AssertEqual(1, report.RowsSkipped, $"Skipped rows after {label}");
        AssertEqual(1, report.Errors.Count, $"Error count after {label}");
        AssertEqual(
            ImportErrorCode.PERMISSION_SENDER_UNAUTHORIZED,
            report.Errors[0].Code,
            $"Error code after {label}");

        // The row must not be in the open or shadow table.
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var openCount = await ctx.CryptoTestItems.IgnoreQueryFilters()
                .CountAsync(i => i.Id == itemId);
            AssertEqual(0, openCount, $"open table count after {label}");

            var shadowCount = await ctx.Database
                .SqlQueryRaw<int>(
                    "SELECT COUNT(*) AS Value FROM _crypto_CryptoTestItems WHERE Id = {0}",
                    itemId)
                .SingleAsync();
            AssertEqual(0, shadowCount, $"shadow table count after {label}");
        }

        Console.WriteLine($"[{Name}] OK: {label} rejected with PERMISSION_SENDER_UNAUTHORIZED");
    }

    // ================================================================
    // Helpers
    // ================================================================

    private async ValueTask<byte[]> ExportItemsDeltaAsync()
    {
        var cryptoHeader = await BuildCryptoHeaderAsync();
        var headerBytes = MessagePackSerializer.Serialize(cryptoHeader);
        try
        {
            var metadata = new BulkExportMetadata
            {
                Mode = 1,
                Tables =
                [
                    new TableExportSpec
                    {
                        TableName = "CryptoTestItems",
                        IsSystemTable = false,
                        Where = "\"UpdatedAt\" > ?",
                        WhereParams = [_sinceCursor.ToString("O", CultureInfo.InvariantCulture)]
                    }
                ]
            };
            return await EncryptedSqliteWasmWorkerBridge.Instance.DeltaExportAsync(
                CryptoDatabaseName, metadata, headerBytes);
        }
        finally
        {
            cryptoHeader.Clear();
        }
    }

    private async ValueTask<byte[]> BuildHeaderBytesAsync()
    {
        var cryptoHeader = await BuildCryptoHeaderAsync();
        try
        {
            return MessagePackSerializer.Serialize(cryptoHeader);
        }
        finally
        {
            cryptoHeader.Clear();
        }
    }

    private async ValueTask<ImportReport> ImportWithHeaderAsync(byte[] headerBytes, byte[] envelopeBytes)
    {
        var reportBytes = await EncryptedSqliteWasmWorkerBridge.Instance.DeltaImportAsync(
            CryptoDatabaseName, headerBytes, envelopeBytes);
        return MessagePackSerializer.Deserialize<ImportReport>(reportBytes);
    }

    private async ValueTask<CryptoHeader> BuildCryptoHeaderAsync()
    {
        await using var ctx = await CryptoFactory.CreateDbContextAsync();
        // IgnoreQueryFilters so the lookup survives sub-cases where the
        // ShareGroup row has been soft-deleted by a prior tamper step.
        // Header construction is admin-side ceremony, not authz.
        var group = await ctx.ShareGroups.IgnoreQueryFilters()
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        var target = await ctx.ShareTargets.IgnoreQueryFilters()
            .SingleAsync(t => t.ShareGroupId == group.Id && t.MemberPublicKey == CryptoTestContext.AdminX25519PublicKey);

        return new CryptoHeader
        {
            Version = 2,
            SystemTables = ["Contacts", "ShareGroups", "ShareTargets"],
            ClientContactId = (await ctx.Contacts.IgnoreQueryFilters().SingleAsync(c => c.IsAdmin)).Id,
            ClientX25519PrivateKey = CryptoTestContext.AdminX25519PrivateKey.AsSpan().ToArray(),
            AdminX25519PublicKey = Convert.FromBase64String(group.GroupAdminPublicKey),
            GroupContext = group.GroupContext,
            KeyVersion = group.KeyVersion,
            WrappedCek = target.WrappedContentKey,
            ClientEd25519PrivateKey = CryptoTestContext.AdminEd25519PrivateKey.AsSpan().ToArray(),
            ClientEd25519PublicKey = Convert.FromBase64String(CryptoTestContext.AdminEd25519PublicKey)
        };
    }

    private async ValueTask ClearItemsOpenTableAsync()
    {
        await using var ctx = await CryptoFactory.CreateDbContextAsync();
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM CryptoTestItems");
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM _crypto_CryptoTestItems");
    }

    private async ValueTask ResetDatabaseAsync()
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
}
