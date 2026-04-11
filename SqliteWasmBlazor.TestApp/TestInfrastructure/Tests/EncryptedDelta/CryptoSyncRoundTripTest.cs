using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync;
using SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EncryptedDelta;

/// <summary>
/// Full encrypted delta round-trip via <see cref="SyncOrchestrator"/>:
/// seed → ExportAsync → clear → ImportAsync → verify. Plus a tamper-negative
/// pass that mutates one byte of the envelope and asserts the importer
/// reports <see cref="ImportErrorCode.TamperSignatureInvalid"/>.
/// </summary>
internal class CryptoSyncRoundTripTest(
    IDbContextFactory<CryptoTestContext> cryptoFactory,
    ISqliteWasmDatabaseService databaseService)
    : CryptoSyncTestBase(cryptoFactory, databaseService)
{
    public override string Name => "CryptoSync_RoundTrip";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // ---------- Arrange ----------
        const int seedCount = 10;
        var sinceCursor = DateTime.UtcNow;
        await Task.Delay(20);

        var expectedIds = new List<Guid>();
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            for (var i = 0; i < seedCount; i++)
            {
                var id = Guid.NewGuid();
                expectedIds.Add(id);
                ctx.CryptoTestItems.Add(new CryptoTestItem
                {
                    Id = id,
                    Title = $"Item-{i}",
                    Description = $"Round-trip test item #{i}",
                    Price = 0.99m + i,
                    IsBought = (i % 2) == 0,
                    SharingScope = SharingScope.Public,
                    SharingId = CryptoSyncBootstrap.SystemSharingId,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            await ctx.SaveChangesAsync();
        }

        // ---------- Export via SyncOrchestrator ----------
        byte[] envelope;
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var orchestrator = new SyncOrchestrator(DatabaseService, ctx);
            var header = await BuildHeaderAsync(ctx);
            envelope = await orchestrator.ExportAsync(
                CryptoDatabaseName, header, sinceCursor);
        }

        if (envelope.Length == 0)
        {
            throw new InvalidOperationException("Orchestrator export returned empty bytes");
        }

        // Envelope sanity: at least one group, and CryptoTestItems present.
        var parsed = MessagePackSerializer.Deserialize<DeltaEnvelope>(envelope);
        if (parsed.Groups.Count == 0)
        {
            throw new InvalidOperationException("Envelope contains no groups");
        }
        var itemsGroup = parsed.Groups.FirstOrDefault(g => g.TableName == "CryptoTestItems")
            ?? throw new InvalidOperationException("Envelope missing CryptoTestItems group");
        if (itemsGroup.Rows.Count != seedCount)
        {
            throw new InvalidOperationException(
                $"Expected {seedCount} rows in CryptoTestItems group, got {itemsGroup.Rows.Count}");
        }

        Console.WriteLine($"[{Name}] Export OK: {envelope.Length} bytes, {parsed.Groups.Count} group(s)");

        // ---------- Clear open + shadow for CryptoTestItems ----------
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            ctx.CryptoTestItems.RemoveRange(
                await ctx.CryptoTestItems.IgnoreQueryFilters().ToListAsync());
            await ctx.SaveChangesAsync();
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM _crypto_CryptoTestItems");

            if (await ctx.CryptoTestItems.IgnoreQueryFilters().CountAsync() != 0)
            {
                throw new InvalidOperationException("Failed to clear open table");
            }
        }

        // ---------- Import via SyncOrchestrator ----------
        ImportReport report;
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var orchestrator = new SyncOrchestrator(DatabaseService, ctx);
            var header = await BuildHeaderAsync(ctx);
            report = await orchestrator.ImportAsync(CryptoDatabaseName, header, envelope);
        }

        if (report.RowsImported != seedCount)
        {
            throw new InvalidOperationException(
                $"Expected {seedCount} rows imported, got {report.RowsImported}");
        }
        if (report.Errors.Count != 0)
        {
            var firstError = report.Errors[0];
            throw new InvalidOperationException(
                $"Unexpected import errors: first = {firstError.Code} on {firstError.Table}:{firstError.RowId} — {firstError.Message}");
        }

        // Open table rehydrated with the seeded rows
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var restored = await ctx.CryptoTestItems.OrderBy(i => i.Title).ToListAsync();
            if (restored.Count != seedCount)
            {
                throw new InvalidOperationException(
                    $"Open table count mismatch: expected {seedCount}, got {restored.Count}");
            }
            var restoredIds = restored.Select(i => i.Id).ToHashSet();
            foreach (var expectedId in expectedIds)
            {
                if (!restoredIds.Contains(expectedId))
                {
                    throw new InvalidOperationException($"Missing expected row {expectedId}");
                }
            }
        }

        Console.WriteLine($"[{Name}] Import OK: {report.RowsImported} rows rehydrated");

        // ---------- Tamper negative ----------
        // Flip one byte deep in the envelope (past the outer signature block)
        // and assert the importer rejects with TamperSignatureInvalid.
        var tampered = (byte[])envelope.Clone();
        tampered[^32] ^= 0xFF;

        ImportReport tamperReport;
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var orchestrator = new SyncOrchestrator(DatabaseService, ctx);
            var header = await BuildHeaderAsync(ctx);
            tamperReport = await orchestrator.ImportAsync(CryptoDatabaseName, header, tampered);
        }

        if (tamperReport.RowsImported != 0)
        {
            throw new InvalidOperationException(
                $"Tampered import should have imported 0 rows, got {tamperReport.RowsImported}");
        }
        if (tamperReport.Errors.Count == 0
            || tamperReport.Errors[0].Code != ImportErrorCode.TamperSignatureInvalid)
        {
            var code = tamperReport.Errors.Count > 0 ? tamperReport.Errors[0].Code.ToString() : "<none>";
            throw new InvalidOperationException(
                $"Tampered import should have reported TamperSignatureInvalid, got {code}");
        }

        Console.WriteLine($"[{Name}] Tamper negative OK: {tamperReport.Errors[0].Code}");
        return "OK";
    }

    private static async Task<V2CryptoHeader> BuildHeaderAsync(CryptoTestContext ctx)
    {
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
}
