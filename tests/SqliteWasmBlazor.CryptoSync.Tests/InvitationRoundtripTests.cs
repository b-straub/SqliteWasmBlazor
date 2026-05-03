using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.BouncyCastle;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// End-to-end invitation flow coverage. Most of the <c>Roundtrip_*</c>
/// tests cover the contact-side <see cref="ContactInvitationService.RespondToInvitationAsync"/>
/// path in isolation; the full admin-side ingestion + promotion is covered
/// in the commit-4 tests.
/// </summary>
public class InvitationRoundtripTests
{
    [Fact]
    public async Task RespondToInvitation_TamperedBundleSignature_ThrowsInvalidInvitationBundle()
    {
        await using var scenario = await TwoActorBootstrap.CreateAsync();
        var relay = new InMemorySyncRelay();
        var contactTransport = new InMemorySyncTransport(relay);

        var bundle = await scenario.Admin.Invitations.CreateInvitationAsync(
            scenario.Admin.Keys, InvitationTestSalt.Default, "Helen", "helen@test.com");
        bundle.AdminSignature[0] ^= 0xFF;

        await Assert.ThrowsAsync<InvalidInvitationBundleException>(
            () => scenario.User.Invitations.RespondToInvitationAsync(
                bundle,
                scenario.User.Keys,
                new ContactUserData { Username = "Helen", Email = "helen@test.com" },
                contactTransport).AsTask());
    }

    [Fact]
    public async Task RespondToInvitation_ExpiredBundle_ThrowsInvitationExpired()
    {
        await using var scenario = await TwoActorBootstrap.CreateAsync();
        var relay = new InMemorySyncRelay();
        var contactTransport = new InMemorySyncTransport(relay);

        // Build a fake expired bundle by re-signing canonical with a past
        // ExpiresAt — using the real signing path so the only failure is the
        // expiry check, not the signature.
        var transportSecret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var transportDual = await scenario.Crypto.DeriveDualKeyPairAsync(transportSecret);
        var groupId = Guid.NewGuid();
        var pastExpiry = DateTime.UtcNow.AddSeconds(-5);
        var canonical = ContactInvitationService.BuildBundleCanonical(
            transportDual.X25519PublicKey, groupId, pastExpiry);
        var adminEdPriv = Convert.FromBase64String(scenario.Admin.Keys.Ed25519PrivateKey);
        var sig = await scenario.Crypto.SignAsync(canonical, adminEdPriv);
        Assert.True(sig.Success);

        var bundle = new InvitationBundle
        {
            TransportSecret = transportSecret,
            GroupId = groupId,
            ExpiresAt = pastExpiry,
            AdminSignature = Convert.FromBase64String(sig.Value!),
            AdminEd25519PublicKey = scenario.Admin.Keys.Ed25519PublicKey,
            AdminX25519PublicKey = scenario.Admin.Keys.X25519PublicKey
        };

        await Assert.ThrowsAsync<InvitationExpiredException>(
            () => scenario.User.Invitations.RespondToInvitationAsync(
                bundle,
                scenario.User.Keys,
                new ContactUserData { Username = "Helen", Email = "helen@test.com" },
                contactTransport).AsTask());
    }

    [Fact]
    public async Task RespondToInvitation_HappyPath_BroadcastsResponseEnvelope()
    {
        await using var scenario = await TwoActorBootstrap.CreateAsync();
        var relay = new InMemorySyncRelay();
        var contactTransport = new InMemorySyncTransport(relay);
        var adminTransport = new InMemorySyncTransport(relay);

        var bundle = await scenario.Admin.Invitations.CreateInvitationAsync(
            scenario.Admin.Keys, InvitationTestSalt.Default, "Helen", "helen@test.com");

        await scenario.User.Invitations.RespondToInvitationAsync(
            bundle,
            scenario.User.Keys,
            new ContactUserData { Username = "Helen", Email = "helen@test.com" },
            contactTransport);

        var wireBytes = await adminTransport.TryReceiveAsync()
            ?? throw new InvalidOperationException("admin transport returned no envelope");
        var envelope = MessagePackSerializer.Deserialize<InvitationResponseEnvelope>(wireBytes);
        Assert.Equal(bundle.GroupId, envelope.GroupId);
        Assert.NotEmpty(envelope.Ciphertext);
        Assert.Equal(12, envelope.Nonce.Length);
    }

    [Fact]
    public async Task RespondToInvitation_WireOpacity_NoPlaintextLeak()
    {
        await using var scenario = await TwoActorBootstrap.CreateAsync();
        var relay = new InMemorySyncRelay();
        var contactTransport = new InMemorySyncTransport(relay);
        var adminTransport = new InMemorySyncTransport(relay);

        var bundle = await scenario.Admin.Invitations.CreateInvitationAsync(
            scenario.Admin.Keys, InvitationTestSalt.Default, "Helen", "helen@test.com");

        await scenario.User.Invitations.RespondToInvitationAsync(
            bundle,
            scenario.User.Keys,
            new ContactUserData { Username = "Helen", Email = "helen@test.com" },
            contactTransport);

        var wireBytes = await adminTransport.TryReceiveAsync()
            ?? throw new InvalidOperationException("admin transport returned no envelope");
        AssertNoPlaintextLeak(wireBytes,
            scenario.User.Keys.X25519PublicKey,
            scenario.User.Keys.Ed25519PublicKey);
    }

    [Fact]
    public async Task RespondToInvitation_AdminCanDecryptResponse()
    {
        await using var scenario = await TwoActorBootstrap.CreateAsync();
        var relay = new InMemorySyncRelay();
        var contactTransport = new InMemorySyncTransport(relay);
        var adminTransport = new InMemorySyncTransport(relay);

        var bundle = await scenario.Admin.Invitations.CreateInvitationAsync(
            scenario.Admin.Keys, InvitationTestSalt.Default, "Helen", "helen@test.com");

        await scenario.User.Invitations.RespondToInvitationAsync(
            bundle,
            scenario.User.Keys,
            new ContactUserData { Username = "Helen", Email = "helen@test.com" },
            contactTransport);

        var wireBytes = await adminTransport.TryReceiveAsync()
            ?? throw new InvalidOperationException("no envelope");
        var envelope = MessagePackSerializer.Deserialize<InvitationResponseEnvelope>(wireBytes);

        // Admin re-derives the wrapping key with their priv + transport pub.
        var transportPub = (await scenario.Crypto.DeriveDualKeyPairAsync(bundle.TransportSecret))
            .X25519PublicKey;
        var groupContext = $"invitation-{bundle.GroupId:N}:v1";
        var adminPriv = Convert.FromBase64String(scenario.Admin.Keys.X25519PrivateKey);
        var wkResult = await scenario.Crypto.DeriveWrappingKeyAsync(adminPriv, transportPub, groupContext);
        Assert.True(wkResult.Success);

        var decResult = await scenario.Crypto.DecryptSymmetricAsync(
            new SymmetricEncryptedData(
                Convert.ToBase64String(envelope.Ciphertext),
                Convert.ToBase64String(envelope.Nonce)),
            wkResult.Value);
        Assert.True(decResult.Success);

        var payload = MessagePackSerializer.Deserialize<InvitationResponsePayload>(
            Convert.FromBase64String(decResult.Value!));
        Assert.Equal(scenario.User.Keys.X25519PublicKey, payload.ContactX25519PublicKey);
        Assert.Equal(scenario.User.Keys.Ed25519PublicKey, payload.ContactEd25519PublicKey);

        // Verify ContactSignature against contact's Ed25519 pub.
        var canonical = ContactInvitationService.BuildContactSignatureCanonical(
            bundle.GroupId, payload.ContactX25519PublicKey, payload.ContactEd25519PublicKey, bundle.ExpiresAt);
        var ok = await scenario.Crypto.VerifyAsync(
            canonical,
            Convert.ToBase64String(payload.ContactSignature),
            payload.ContactEd25519PublicKey);
        Assert.True(ok);
    }

    [Fact]
    public async Task FullE2E_CreateRespondIngestPromote_BindsTrustedContactAndDeletesChannel()
    {
        var crypto = new BouncyCastleCryptoProvider();
        var admin = await TestActor.CreateAsync("Admin", isAdmin: true, seedByte: 1, crypto);
        var helen = await TestActor.CreateAsync("Helen", isAdmin: false, seedByte: 240, crypto);
        try
        {
            var relay = new InMemorySyncRelay();
            var adminTransport = new InMemorySyncTransport(relay);
            var helenTransport = new InMemorySyncTransport(relay);

            // 1. Admin creates the invitation channel.
            var bundle = await admin.Invitations.CreateInvitationAsync(
                admin.Keys, InvitationTestSalt.Default, "Helen", "helen@test.com");

            // 2. Helen responds (bundle delivered out-of-band).
            await helen.Invitations.RespondToInvitationAsync(
                bundle, helen.Keys,
                new ContactUserData { Username = "Helen", Email = "helen@test.com" },
                helenTransport);

            // Wire-opacity check: pull bytes from relay before admin drains.
            var leakBytes = await adminTransport.TryReceiveAsync()
                ?? throw new InvalidOperationException("no envelope");
            AssertNoPlaintextLeak(leakBytes,
                helen.Keys.X25519PublicKey, helen.Keys.Ed25519PublicKey);

            // Repost so admin can still ingest. (TryReceive consumed it.)
            await helen.Invitations.RespondToInvitationAsync(
                bundle, helen.Keys,
                new ContactUserData { Username = "Helen", Email = "helen@test.com" },
                helenTransport);

            // 3. Admin ingests the response.
            var ingested = await admin.Invitations.IngestInvitationResponsesAsync(
                admin.Keys, adminTransport);
            Assert.Equal(1, ingested);

            var row = await admin.Context.Invitations.SingleAsync(i => i.Id == bundle.GroupId);
            Assert.Equal(helen.Keys.X25519PublicKey, row.ContactX25519PublicKey);
            Assert.Equal(helen.Keys.Ed25519PublicKey, row.ContactEd25519PublicKey);
            Assert.NotNull(row.ContactSignature);

            // 4. Admin promotes.
            var helenContact = await admin.Invitations.PromoteInvitationAsync(
                bundle.GroupId, admin.Keys, InvitationTestSalt.Default);
            Assert.False(helenContact.IsAdmin);
            Assert.Equal(helen.Keys.X25519PublicKey, helenContact.X25519PublicKey);
            Assert.Equal(SharingScope.PUBLIC, helenContact.SharingScope);

            // System-group ShareTarget for Helen exists.
            var systemGroup = await admin.Context.ShareGroups
                .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
            Assert.True(await admin.Context.ShareTargets
                .AnyAsync(t => t.ShareGroupId == systemGroup.Id
                    && t.MemberPublicKey == helen.Keys.X25519PublicKey));

            // Helen's self-group ShareGroup + ShareTarget exist.
            var helenSelfGroup = await admin.Context.ShareGroups
                .SingleAsync(g => g.GroupAdminPublicKey == helen.Keys.X25519PublicKey);
            Assert.True(await admin.Context.ShareTargets
                .AnyAsync(t => t.ShareGroupId == helenSelfGroup.Id));

            // Privacy invariant: admin can't unwrap Helen's self-group CEK.
            var helenSelfTarget = await admin.Context.ShareTargets
                .SingleAsync(t => t.ShareGroupId == helenSelfGroup.Id);
            var wrapped = CryptoSyncBootstrap.DeserializeWrappedCek(helenSelfTarget.WrappedContentKey);
            var adminPriv = Convert.FromBase64String(admin.Keys.X25519PrivateKey);
            var wrongWk = await crypto.DeriveWrappingKeyAsync(
                adminPriv, helen.Keys.X25519PublicKey, helenSelfGroup.GroupContext);
            Assert.True(wrongWk.Success);
            var unwrapResult = await crypto.UnwrapContentKeyAsync(wrapped, wrongWk.Value);
            Assert.False(unwrapResult.Success);

            // Invitation channel rows all gone.
            Assert.Empty(await admin.Context.Invitations.ToListAsync());
            Assert.Empty(await admin.Context.ShareGroups
                .Where(g => g.GroupContext.StartsWith("invitation-")).ToListAsync());
        }
        finally
        {
            await admin.DisposeAsync();
            await helen.DisposeAsync();
        }
    }

    [Fact]
    public async Task Roundtrip_ImportFiresNotifier()
    {
        var crypto = new BouncyCastleCryptoProvider();
        var actor = await TestActor.CreateAsync("Solo", isAdmin: true, seedByte: 230, crypto);
        try
        {
            var notifier = new RecordingImportNotifier();
            var fakeDatabase = new FakeDatabaseService
            {
                CannedImportReport = new ImportReport { RowsImported = 3 }
            };

            var orchestrator = new SyncOrchestrator(fakeDatabase, actor.Context, notifier);
            var header = new V2CryptoHeader
            {
                Version = 2,
                SystemTables = ["Contacts", "ShareGroups", "ShareTargets"],
                GroupContext = CryptoSyncBootstrap.SystemGroupContext,
                KeyVersion = 1
            };

            var report = await orchestrator.ImportAsync("test.db", header, envelopeBytes: [0x01]);

            Assert.Equal(3, report.RowsImported);
            Assert.Single(notifier.Reports);
            Assert.Equal(3, notifier.Reports.Single().RowsImported);
        }
        finally
        {
            await actor.DisposeAsync();
        }
    }

    /// <summary>
    /// Treat the wire bytes as Latin-1 / ASCII and refuse any of the
    /// caller-supplied plaintext markers to appear as a substring. Catches
    /// the common "we serialized but forgot to encrypt" regression cleanly
    /// without reaching for entropy heuristics.
    /// </summary>
    private static void AssertNoPlaintextLeak(byte[] wire, params string[] markers)
    {
        var wireText = System.Text.Encoding.Latin1.GetString(wire);
        foreach (var marker in markers)
        {
            Assert.DoesNotContain(marker, wireText);
        }
    }
}
