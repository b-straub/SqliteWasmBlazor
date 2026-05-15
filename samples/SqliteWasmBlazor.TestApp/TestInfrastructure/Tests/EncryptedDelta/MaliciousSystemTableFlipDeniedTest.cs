using System.Globalization;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.CryptoSync;
using SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EncryptedDelta;

/// <summary>
/// F-001 regression — receiver MUST classify system tables from its own
/// trusted <see cref="CryptoHeader.SystemTables"/>, not from the wire tuple's
/// <c>group[1]</c> bit.
///
/// <para>Attack model: a member with a valid group CEK exports a Contacts
/// row with <c>TableExportSpec.IsSystemTable = false</c>. Without the fix
/// the receiver trusted the wire bit, skipped the admin-only gate, and fell
/// through to the role-based path — where Contacts has no Permissions row
/// and "no permission row = full access" let the row import. With the fix
/// the receiver consults its own <see cref="CryptoHeader.SystemTables"/>
/// list, recognizes Contacts as system, runs <c>verifySenderIsAdmin</c>, and
/// rejects non-admin senders.</para>
///
/// <para>Simulation: the only signing keypair available in the test fixture
/// is the admin's, so we demote the admin in the Contacts table (set
/// <c>IsAdmin = 0</c>) immediately before import — that flips the lookup
/// <c>WHERE IsAdmin = 1</c> to "no admin row exists" and makes the sender
/// (whose Ed25519 still matches admin's actual pubkey) fail the admin gate
/// for receiver-side purposes. The wire bit is set to false to encode the
/// flip; the assertion that proves the fix is the PERMISSION_INSERT_DENIED
/// code with the system-table message, which the admin gate emits — not the
/// PERMISSION_SENDER_UNAUTHORIZED code emitted by the role-based fallback
/// path (which would never be reached by a Contacts mutation post-fix).</para>
/// </summary>
internal class MaliciousSystemTableFlipDeniedTest(
    IDbContextFactory<CryptoTestContext> cryptoFactory,
    ISqliteWasmDatabaseService databaseService,
    IEncryptedSqliteWasmDatabaseService session)
    : CryptoSyncTestBase(cryptoFactory, databaseService, session)
{
    public override string Name => "CryptoSync_MaliciousSystemTableFlipDenied";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        Console.WriteLine($"[{Name}] Step: malicious member flips Contacts to non-system → admin gate must still reject");

        // Snapshot the cursor strictly before inserting Mallory so the export
        // walks _column_registry with WHERE UpdatedAt > cursor and picks up
        // only that row (not the seeded admin contact).
        var sinceCursor = DateTime.UtcNow;
        await Task.Delay(20);

        var malloryId = Guid.NewGuid();
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            ctx.Contacts.Add(new TrustedContact
            {
                Id = malloryId,
                Username = "Mallory",
                Email = "mallory@evil.test",
                X25519PublicKey = MakeFakeBase64Key(0x11),
                Ed25519PublicKey = MakeFakeBase64Key(0x22),
                IsAdmin = false,
                UpdatedAt = DateTime.UtcNow,
                SharingScope = SharingScope.PUBLIC,
                SharingId = CryptoSyncBootstrap.SystemSharingId
            });
            await ctx.SaveChangesAsync();
        }

        var delta = await ExportMaliciousFlipAsync(sinceCursor);

        // Remove the planted row from BOTH the open Contacts table and the
        // shadow _crypto_Contacts table (the export side stamped the latter).
        // We're simulating a receiver under attack — they have no shadow
        // row to start with. Without this wipe, the post-import shadow
        // assertion would observe the export-side row and misreport that
        // the receiver accepted the malicious envelope.
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM Contacts WHERE Id = {malloryId}");
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM _crypto_Contacts WHERE Id = {malloryId}");
        }

        await DemoteAdminAsync();

        var report = await ImportDeltaAsync(delta);

        AssertEqual(0, report.RowsImported, "Imported rows after malicious flip");
        AssertEqual(1, report.RowsSkipped, "Skipped rows after malicious flip");
        AssertEqual(1, report.Errors.Count, "Error count after malicious flip");
        AssertEqual(
            ImportErrorCode.PERMISSION_INSERT_DENIED,
            report.Errors[0].Code,
            "Error code after malicious flip");
        if (!report.Errors[0].Message.Contains("system table Contacts", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected admin-gate message mentioning 'system table Contacts', got: {report.Errors[0].Message}");
        }

        // Mallory must not have landed in Contacts (open OR shadow).
        await using (var ctx = await CryptoFactory.CreateDbContextAsync())
        {
            var openCount = await ctx.Contacts.IgnoreQueryFilters()
                .CountAsync(c => c.Id == malloryId);
            AssertEqual(0, openCount, "Mallory row in open Contacts");

            var shadowCount = await ctx.Database
                .SqlQueryRaw<int>(
                    "SELECT COUNT(*) AS Value FROM _crypto_Contacts WHERE Id = {0}",
                    malloryId)
                .SingleAsync();
            AssertEqual(0, shadowCount, "Mallory row in shadow _crypto_Contacts");
        }

        Console.WriteLine($"[{Name}] OK: wire bit ignored, local-truth admin gate rejected Mallory");
        return "OK";
    }

    // ================================================================
    // Helpers
    // ================================================================

    /// <summary>
    /// Build a deterministic-looking 32-byte Base64 string so the test's
    /// fake-pubkey columns have valid Base64 shape but distinct content
    /// from the real admin keys. The cryptographic value is irrelevant —
    /// the row is rejected before any signature operation touches it.
    /// </summary>
    private static string MakeFakeBase64Key(byte seed)
    {
        var bytes = new byte[32];
        for (var i = 0; i < 32; i++)
        {
            bytes[i] = (byte)(seed + i);
        }
        return Convert.ToBase64String(bytes);
    }

    private async ValueTask<byte[]> ExportMaliciousFlipAsync(DateTime sinceCursor)
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
                        TableName = "Contacts",
                        // F-001 attack: claim a known system table is not a
                        // system table. Receiver MUST ignore this bit and
                        // derive system status from CryptoHeader.SystemTables.
                        IsSystemTable = false,
                        Where = "\"UpdatedAt\" > ?",
                        WhereParams = [sinceCursor.ToString("O", CultureInfo.InvariantCulture)]
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

    private async ValueTask<ImportReport> ImportDeltaAsync(byte[] envelopeBytes)
    {
        var cryptoHeader = await BuildCryptoHeaderAsync();
        var headerBytes = MessagePackSerializer.Serialize(cryptoHeader);
        byte[] reportBytes;
        try
        {
            reportBytes = await EncryptedSqliteWasmWorkerBridge.Instance.DeltaImportAsync(
                CryptoDatabaseName, headerBytes, envelopeBytes);
        }
        finally
        {
            cryptoHeader.Clear();
        }
        return MessagePackSerializer.Deserialize<ImportReport>(reportBytes);
    }

    private async ValueTask<CryptoHeader> BuildCryptoHeaderAsync()
    {
        await using var ctx = await CryptoFactory.CreateDbContextAsync();
        var group = await ctx.ShareGroups.SingleAsync(g =>
            g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        var target = await ctx.ShareTargets.SingleAsync(t =>
            t.ShareGroupId == group.Id && t.MemberPublicKey == CryptoTestContext.AdminX25519PublicKey);

        return new CryptoHeader
        {
            Version = 2,
            SystemTables = ["Contacts", "ShareGroups", "ShareTargets"],
            ClientContactId = (await ctx.Contacts.IgnoreQueryFilters()
                .SingleAsync(c => c.Ed25519PublicKey == CryptoTestContext.AdminEd25519PublicKey)).Id,
            ClientX25519PrivateKey = CryptoTestContext.AdminX25519PrivateKey.AsSpan().ToArray(),
            AdminX25519PublicKey = Convert.FromBase64String(group.GroupAdminPublicKey),
            GroupContext = group.GroupContext,
            KeyVersion = group.KeyVersion,
            WrappedCek = target.WrappedContentKey,
            ClientEd25519PrivateKey = CryptoTestContext.AdminEd25519PrivateKey.AsSpan().ToArray(),
            ClientEd25519PublicKey = Convert.FromBase64String(CryptoTestContext.AdminEd25519PublicKey)
        };
    }

    /// <summary>
    /// Flip the seeded admin Contact's <c>IsAdmin</c> bit to 0. The worker's
    /// <c>verifySenderIsAdmin</c> looks up <c>WHERE IsAdmin = 1</c>; with no
    /// row matching, the sender (whose Ed25519 actually IS the admin's)
    /// fails the gate from the receiver's perspective. We resolve via raw
    /// SQL because the seeded row uses <c>HasData</c> shadow state; EF would
    /// track it via Find but the raw UPDATE keeps the change minimal.
    /// </summary>
    private async ValueTask DemoteAdminAsync()
    {
        await using var ctx = await CryptoFactory.CreateDbContextAsync();
        await ctx.Database.ExecuteSqlRawAsync(
            "UPDATE Contacts SET IsAdmin = 0 WHERE IsAdmin = 1");
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
        }
    }
}
