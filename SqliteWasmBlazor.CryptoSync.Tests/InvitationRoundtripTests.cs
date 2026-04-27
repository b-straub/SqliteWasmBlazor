using SqliteWasmBlazor.Crypto.Abstractions.Services;
using SqliteWasmBlazor.Crypto.Testing;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Stage-1 seam coverage: drive the contact-invitation flow through
/// <see cref="IInvitationChannel"/> instead of an in-process payload pass,
/// and prove <see cref="SyncOrchestrator"/> fires <see cref="IImportNotifier"/>
/// once per import. The channel is byte-opaque, so the privacy invariant
/// from <see cref="ContactInvitationServiceTests"/> must remain intact when
/// the payload travels through it.
/// </summary>
public class InvitationRoundtripTests
{
    [Fact]
    public async Task Roundtrip_ContactBuildsAdminAccepts_TrustedContactPersisted()
    {
        var crypto = new BouncyCastleCryptoProvider();
        var groupEncryption = new GroupEncryptionService(crypto);
        var declarationSigner = new DeclarationSigner(crypto);
        var channel = new FakeInvitationChannel();

        var admin = await TestActor.CreateAsync("Admin", isAdmin: true, seedByte: 1, crypto);
        var contact = await TestActor.CreateAsync("Helen", isAdmin: false, seedByte: 220, crypto);
        try
        {
            // Contact builds the signed payload, MessagePack-serializes,
            // hands the bytes to the channel.
            var contactSvc = new ContactInvitationService(contact.Context, groupEncryption, crypto, declarationSigner);
            var payload = await contactSvc.BuildInvitationResponseAsync(
                contact.Keys,
                new ContactUserData { Username = "Helen", Email = "helen@test.com" });
            await channel.SendAsync(MessagePackSerializer.Serialize(payload));

            // Admin receives the bytes, deserializes, accepts.
            var receivedBytes = await channel.TryReceiveAsync()
                ?? throw new InvalidOperationException("Channel returned no payload");
            var receivedPayload = MessagePackSerializer.Deserialize<ContactAcceptancePayload>(receivedBytes);

            var adminSvc = new ContactInvitationService(admin.Context, groupEncryption, crypto, declarationSigner);
            var persisted = await adminSvc.AcceptInvitationResponseAsync(
                admin.Keys,
                receivedPayload,
                systemRole: SyncRole.EDITOR);

            Assert.Equal(ContactStatus.Verified, persisted.Status);
            Assert.False(persisted.IsAdmin);
            Assert.Equal(contact.Keys.X25519PublicKey, persisted.X25519PublicKey);

            var systemGroup = await admin.Context.ShareGroups
                .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
            var contactSystemTarget = await admin.Context.ShareTargets
                .SingleAsync(t => t.ShareGroupId == systemGroup.Id
                    && t.MemberPublicKey == contact.Keys.X25519PublicKey);
            Assert.Equal(SyncRole.EDITOR, contactSystemTarget.Role);

            var contactSelfGroup = await admin.Context.ShareGroups
                .SingleAsync(g => g.Id == receivedPayload.SelfGroupId);
            Assert.True(await admin.Context.ShareTargets
                .AnyAsync(t => t.ShareGroupId == contactSelfGroup.Id
                    && t.MemberPublicKey == contact.Keys.X25519PublicKey));

            // Channel must be drained.
            Assert.Null(await channel.TryReceiveAsync());
        }
        finally
        {
            await admin.DisposeAsync();
            await contact.DisposeAsync();
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

    [Fact]
    public async Task PrivacyInvariant_AdminCannotUnwrapContactSelfGroupCek()
    {
        var crypto = new BouncyCastleCryptoProvider();
        var groupEncryption = new GroupEncryptionService(crypto);
        var declarationSigner = new DeclarationSigner(crypto);
        var channel = new FakeInvitationChannel();

        var admin = await TestActor.CreateAsync("Admin", isAdmin: true, seedByte: 1, crypto);
        var contact = await TestActor.CreateAsync("Ivy", isAdmin: false, seedByte: 240, crypto);
        try
        {
            var contactSvc = new ContactInvitationService(contact.Context, groupEncryption, crypto, declarationSigner);
            var payload = await contactSvc.BuildInvitationResponseAsync(
                contact.Keys,
                new ContactUserData { Username = "Ivy", Email = "ivy@test.com" });
            await channel.SendAsync(MessagePackSerializer.Serialize(payload));

            var receivedBytes = await channel.TryReceiveAsync()
                ?? throw new InvalidOperationException("Channel returned no payload");
            var receivedPayload = MessagePackSerializer.Deserialize<ContactAcceptancePayload>(receivedBytes);

            var adminSvc = new ContactInvitationService(admin.Context, groupEncryption, crypto, declarationSigner);
            await adminSvc.AcceptInvitationResponseAsync(admin.Keys, receivedPayload);

            // Admin holds the contact's self-group rows but cannot unwrap the CEK.
            var selfGroup = await admin.Context.ShareGroups.SingleAsync(g => g.Id == receivedPayload.SelfGroupId);
            var selfTarget = await admin.Context.ShareTargets.SingleAsync(t => t.ShareGroupId == selfGroup.Id);
            var wrapped = CryptoSyncBootstrap.DeserializeWrappedCek(selfTarget.WrappedContentKey);

            var adminPriv = Convert.FromBase64String(admin.Keys.X25519PrivateKey);
            var wrongWk = await crypto.DeriveWrappingKeyAsync(
                adminPriv, contact.Keys.X25519PublicKey, selfGroup.GroupContext);
            Assert.True(wrongWk.Success);

            var cekResult = await crypto.UnwrapContentKeyAsync(wrapped, wrongWk.Value!);
            Assert.False(cekResult.Success);
        }
        finally
        {
            await admin.DisposeAsync();
            await contact.DisposeAsync();
        }
    }
}
