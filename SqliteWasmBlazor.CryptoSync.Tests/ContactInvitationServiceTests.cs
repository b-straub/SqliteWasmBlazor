using System.Reflection;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using SqliteWasmBlazor.Crypto.Testing;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for <see cref="ContactInvitationService"/>: signed-payload build on
/// the contact device, transactional accept on the admin device, privacy
/// invariant (admin cannot unwrap contact's self-group CEK), and signature
/// verification paths.
/// </summary>
public class ContactInvitationServiceTests : IAsyncLifetime
{
    private TwoActorBootstrap _scenario = null!;

    public async Task InitializeAsync()
    {
        _scenario = await TwoActorBootstrap.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        await _scenario.DisposeAsync();
    }

    // ----------------------------------------------------------------
    // BuildInvitationResponseAsync — contact-side
    // ----------------------------------------------------------------

    [Fact]
    public async Task BuildInvitationResponse_ProducesSignedPayload()
    {
        // Use a throw-away third actor so the bootstrap fixture stays clean.
        var crypto = new BouncyCastleCryptoProvider();
        var groupEncryption = new GroupEncryptionService(crypto);
        var newUser = await TestActor.CreateAsync("Charlie", isAdmin: false, seedByte: 200, crypto);
        try
        {
            var service = new ContactInvitationService(newUser.Context, groupEncryption, crypto, new DeclarationSigner(crypto));

            var payload = await service.BuildInvitationResponseAsync(
                newUser.Keys,
                new ContactUserData { Username = "Charlie", Email = "charlie@test.com" });

            Assert.NotEqual(Guid.Empty, payload.ContactId);
            Assert.Equal(newUser.Keys.X25519PublicKey, payload.X25519PublicKey);
            Assert.Equal(newUser.Keys.Ed25519PublicKey, payload.Ed25519PublicKey);
            Assert.Equal(CryptoSyncBootstrap.BuildSelfGroupContext(payload.ContactId), payload.SelfGroupContext);
            Assert.NotEmpty(payload.SelfWrappedContentKey);
            Assert.NotEmpty(payload.AcceptancePayloadSignature);

            // Positive verification — signature validates against the contact's Ed25519 pub.
            var savedSig = payload.AcceptancePayloadSignature;
            payload.AcceptancePayloadSignature = [];
            var canonical = Convert.ToBase64String(MessagePackSerializer.Serialize(payload));
            payload.AcceptancePayloadSignature = savedSig;
            var ok = await crypto.VerifyAsync(canonical, Convert.ToBase64String(savedSig), newUser.Keys.Ed25519PublicKey);
            Assert.True(ok);
        }
        finally
        {
            await newUser.DisposeAsync();
        }
    }

    [Fact]
    public async Task BuildInvitationResponse_SelfGroupCekUnwrappableByContact()
    {
        var crypto = new BouncyCastleCryptoProvider();
        var groupEncryption = new GroupEncryptionService(crypto);
        var newUser = await TestActor.CreateAsync("Dave", isAdmin: false, seedByte: 201, crypto);
        try
        {
            var service = new ContactInvitationService(newUser.Context, groupEncryption, crypto, new DeclarationSigner(crypto));

            var payload = await service.BuildInvitationResponseAsync(
                newUser.Keys,
                new ContactUserData { Username = "Dave", Email = "dave@test.com" });

            // Contact re-derives the self-ECDH wrapping key and unwraps → valid 32-byte CEK.
            var wrapped = CryptoSyncBootstrap.DeserializeWrappedCek(payload.SelfWrappedContentKey);
            var privKey = Convert.FromBase64String(newUser.Keys.X25519PrivateKey);
            var wk = await crypto.DeriveWrappingKeyAsync(privKey, newUser.Keys.X25519PublicKey, payload.SelfGroupContext);
            Assert.True(wk.Success);

            var cek = await crypto.UnwrapContentKeyAsync(wrapped, wk.Value!);
            Assert.True(cek.Success);
            Assert.Equal(32, cek.Value!.Length);
        }
        finally
        {
            await newUser.DisposeAsync();
        }
    }

    // ----------------------------------------------------------------
    // AcceptInvitationResponseAsync — admin-side persistence
    // ----------------------------------------------------------------

    [Fact]
    public async Task AcceptInvitationResponse_ValidPayload_InsertsContactAndTargetRows()
    {
        // The fixture already completed one invitation flow (admin ↔ user).
        // Assert the resulting rows exist exactly as spec'd.
        var userContact = await _scenario.Admin.Context.Contacts
            .SingleAsync(c => c.X25519PublicKey == _scenario.User.Keys.X25519PublicKey);
        Assert.False(userContact.IsAdmin);
        Assert.Equal(SharingScope.PUBLIC, userContact.SharingScope);
        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, userContact.SharingId);

        var systemGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        var userSystemTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == _scenario.User.Keys.X25519PublicKey);
        Assert.Equal(SyncRole.VIEWER, userSystemTarget.Role);
        Assert.NotEmpty(userSystemTarget.WrappedContentKey);

        var userSelfGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext.StartsWith("self-")
                && g.GroupAdminPublicKey == _scenario.User.Keys.X25519PublicKey);
        var userSelfTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == userSelfGroup.Id);
        Assert.Equal(SyncRole.OWNER, userSelfTarget.Role);
        Assert.Equal(userContact.Id, userSelfTarget.GrantedByContactId);
    }

    [Fact]
    public async Task AcceptInvitationResponse_SelfGroupRowsRouteViaSystem()
    {
        // The contact's self-group ShareGroup + ShareTarget must carry
        // SharingId = "system" even though their SharingScope = Client,
        // because they are [SystemTable]-routed transport rows.
        var userSelfGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext.StartsWith("self-")
                && g.GroupAdminPublicKey == _scenario.User.Keys.X25519PublicKey);
        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, userSelfGroup.SharingId);
        Assert.Equal(SharingScope.CLIENT, userSelfGroup.SharingScope);

        var userSelfTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == userSelfGroup.Id);
        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, userSelfTarget.SharingId);
        Assert.Equal(SharingScope.CLIENT, userSelfTarget.SharingScope);
    }

    // ----------------------------------------------------------------
    // The privacy claim — admin CANNOT unwrap the contact's self-group CEK
    // ----------------------------------------------------------------

    [Fact]
    public async Task AcceptInvitationResponse_AdminCannotUnwrapContactSelfGroupCek()
    {
        var userSelfGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext.StartsWith("self-")
                && g.GroupAdminPublicKey == _scenario.User.Keys.X25519PublicKey);
        var userSelfTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == userSelfGroup.Id);

        var wrapped = CryptoSyncBootstrap.DeserializeWrappedCek(userSelfTarget.WrappedContentKey);

        // Admin attempts to re-derive the wrapping key using their OWN private
        // key against the contact's public key. This produces a DIFFERENT
        // ECDH shared secret than HKDF(ECDH(userPriv, userPub)), so the
        // resulting wrapping key does not unwrap the CEK.
        var adminPriv = Convert.FromBase64String(_scenario.Admin.Keys.X25519PrivateKey);
        var wrongWk = await _scenario.Crypto.DeriveWrappingKeyAsync(
            adminPriv,
            _scenario.User.Keys.X25519PublicKey,
            userSelfGroup.GroupContext);
        Assert.True(wrongWk.Success);

        // AES-GCM with the wrong key → authenticated decryption must fail.
        // PrfResult returns Success = false (not an exception).
        var cekResult = await _scenario.Crypto.UnwrapContentKeyAsync(wrapped, wrongWk.Value!);
        Assert.False(cekResult.Success);
    }

    [Fact]
    public async Task AcceptInvitationResponse_ContactCanUnwrapOwnSelfGroupCek()
    {
        // Positive control for the privacy claim — the contact CAN unwrap.
        var userSelfGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext.StartsWith("self-")
                && g.GroupAdminPublicKey == _scenario.User.Keys.X25519PublicKey);
        var userSelfTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == userSelfGroup.Id);

        var wrapped = CryptoSyncBootstrap.DeserializeWrappedCek(userSelfTarget.WrappedContentKey);
        var userPriv = Convert.FromBase64String(_scenario.User.Keys.X25519PrivateKey);
        var wk = await _scenario.Crypto.DeriveWrappingKeyAsync(
            userPriv,
            _scenario.User.Keys.X25519PublicKey,
            userSelfGroup.GroupContext);
        Assert.True(wk.Success);

        var cek = await _scenario.Crypto.UnwrapContentKeyAsync(wrapped, wk.Value!);
        Assert.True(cek.Success);
        Assert.Equal(32, cek.Value!.Length);
    }

    // ----------------------------------------------------------------
    // Signature verification failure paths
    // ----------------------------------------------------------------

    [Fact]
    public async Task AcceptInvitationResponse_TamperedPayload_ThrowsSignatureError()
    {
        var crypto = new BouncyCastleCryptoProvider();
        var groupEncryption = new GroupEncryptionService(crypto);
        var newUser = await TestActor.CreateAsync("Eve", isAdmin: false, seedByte: 210, crypto);
        try
        {
            var userSvc = new ContactInvitationService(newUser.Context, groupEncryption, crypto, new DeclarationSigner(crypto));
            var payload = await userSvc.BuildInvitationResponseAsync(
                newUser.Keys,
                new ContactUserData { Username = "Eve", Email = "eve@test.com" });

            // Tamper a non-signature field after signing.
            typeof(ContactAcceptancePayload)
                .GetProperty(nameof(ContactAcceptancePayload.Username))!
                .SetValue(payload, "Mallory");

            var adminSvc = new ContactInvitationService(_scenario.Admin.Context, groupEncryption, crypto, new DeclarationSigner(crypto));
            await Assert.ThrowsAsync<InvalidContactSignatureException>(() =>
                adminSvc.AcceptInvitationResponseAsync(_scenario.Admin.Keys, payload).AsTask());
        }
        finally
        {
            await newUser.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptInvitationResponse_TamperedSignature_Throws()
    {
        var crypto = new BouncyCastleCryptoProvider();
        var groupEncryption = new GroupEncryptionService(crypto);
        var newUser = await TestActor.CreateAsync("Frank", isAdmin: false, seedByte: 211, crypto);
        try
        {
            var userSvc = new ContactInvitationService(newUser.Context, groupEncryption, crypto, new DeclarationSigner(crypto));
            var payload = await userSvc.BuildInvitationResponseAsync(
                newUser.Keys,
                new ContactUserData { Username = "Frank", Email = "frank@test.com" });

            // Flip a byte in the signature itself.
            payload.AcceptancePayloadSignature[0] ^= 0xFF;

            var adminSvc = new ContactInvitationService(_scenario.Admin.Context, groupEncryption, crypto, new DeclarationSigner(crypto));
            await Assert.ThrowsAsync<InvalidContactSignatureException>(() =>
                adminSvc.AcceptInvitationResponseAsync(_scenario.Admin.Keys, payload).AsTask());
        }
        finally
        {
            await newUser.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptInvitationResponse_EmptySignature_Throws()
    {
        var crypto = new BouncyCastleCryptoProvider();
        var groupEncryption = new GroupEncryptionService(crypto);
        var newUser = await TestActor.CreateAsync("Grace", isAdmin: false, seedByte: 212, crypto);
        try
        {
            var userSvc = new ContactInvitationService(newUser.Context, groupEncryption, crypto, new DeclarationSigner(crypto));
            var payload = await userSvc.BuildInvitationResponseAsync(
                newUser.Keys,
                new ContactUserData { Username = "Grace", Email = "grace@test.com" });
            payload.AcceptancePayloadSignature = [];

            var adminSvc = new ContactInvitationService(_scenario.Admin.Context, groupEncryption, crypto, new DeclarationSigner(crypto));
            await Assert.ThrowsAsync<InvalidContactSignatureException>(() =>
                adminSvc.AcceptInvitationResponseAsync(_scenario.Admin.Keys, payload).AsTask());
        }
        finally
        {
            await newUser.DisposeAsync();
        }
    }

    // ----------------------------------------------------------------
    // CreateInvitationAsync — admin-initiated invitation channel
    // ----------------------------------------------------------------

    [Fact]
    public async Task CreateInvitationAsync_PersistsShareGroupAndTwoShareTargets()
    {
        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, "Bob", "bob@test.com");

        var groupContext = $"invitation-{bundle.GroupId:N}:v1";
        var group = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == groupContext);
        Assert.Equal(bundle.GroupId, group.Id);
        Assert.Equal(_scenario.Admin.Keys.X25519PublicKey, group.GroupAdminPublicKey);

        var targets = await _scenario.Admin.Context.ShareTargets
            .Where(t => t.ShareGroupId == group.Id)
            .ToListAsync();
        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.MemberPublicKey == _scenario.Admin.Keys.X25519PublicKey);
        // Other ShareTarget is for the transport pubkey, not yet known by name.
        Assert.Single(targets, t => t.MemberPublicKey != _scenario.Admin.Keys.X25519PublicKey);
    }

    [Fact]
    public async Task CreateInvitationAsync_PersistsInvitationRow_PendingState()
    {
        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, "Bob", "bob@test.com", "Bob from accounting");

        var groupContext = $"invitation-{bundle.GroupId:N}:v1";
        var invitation = await _scenario.Admin.Context.Invitations
            .SingleAsync(i => i.SharingId == groupContext);
        Assert.Equal("Bob", invitation.Username);
        Assert.Equal("bob@test.com", invitation.Email);
        Assert.Equal("Bob from accounting", invitation.Comment);
        Assert.Null(invitation.ContactX25519PublicKey);
        Assert.Null(invitation.ContactEd25519PublicKey);
        Assert.Null(invitation.ContactSignature);
        Assert.Null(invitation.SelfGroupId);
        Assert.Equal(SharingScope.SHARED, invitation.SharingScope);
        Assert.Equal(bundle.ExpiresAt, invitation.ExpiresAt);
    }

    [Fact]
    public async Task CreateInvitationAsync_TransportSecret_DerivesSameKeypairOnBothSides()
    {
        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, "Bob");

        // Admin re-derives the transport pubkey from the secret — must match
        // the ShareTarget the admin just persisted.
        var derived = await _scenario.Crypto.DeriveX25519KeyPairAsync(bundle.TransportSecret);

        var groupContext = $"invitation-{bundle.GroupId:N}:v1";
        var group = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == groupContext);
        Assert.Contains(_scenario.Admin.Context.ShareTargets,
            t => t.ShareGroupId == group.Id && t.MemberPublicKey == derived.PublicKeyBase64);
    }

    [Fact]
    public async Task CreateInvitationAsync_BundleSignatureVerifiesAgainstAdminPubKey()
    {
        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, "Bob");

        var derived = await _scenario.Crypto.DeriveX25519KeyPairAsync(bundle.TransportSecret);
        var canonical = ContactInvitationService.BuildBundleCanonical(
            derived.PublicKeyBase64, bundle.GroupId, bundle.ExpiresAt);

        var ok = await _scenario.Crypto.VerifyAsync(
            canonical,
            Convert.ToBase64String(bundle.AdminSignature),
            bundle.AdminEd25519PublicKey);
        Assert.True(ok);
    }

    [Fact]
    public async Task CreateInvitationAsync_DefaultTtl_TwentyFourHours()
    {
        var before = DateTime.UtcNow;
        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, "Bob");
        var after = DateTime.UtcNow;

        var minExpected = before + TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1);
        var maxExpected = after + TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1);
        Assert.InRange(bundle.ExpiresAt, minExpected, maxExpected);
    }

    [Fact]
    public async Task CreateInvitationAsync_BlankUsername_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _scenario.Admin.Invitations.CreateInvitationAsync(_scenario.Admin.Keys, "  ").AsTask());
    }

    [Fact]
    public async Task RevokeInvitationAsync_DeletesShareGroupAndRow()
    {
        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, "Bob");
        var groupContext = $"invitation-{bundle.GroupId:N}:v1";
        var invitationId = (await _scenario.Admin.Context.Invitations
            .SingleAsync(i => i.SharingId == groupContext)).Id;

        await _scenario.Admin.Invitations.RevokeInvitationAsync(invitationId);

        Assert.Empty(await _scenario.Admin.Context.Invitations
            .Where(i => i.SharingId == groupContext).ToListAsync());
        Assert.Empty(await _scenario.Admin.Context.ShareGroups
            .Where(g => g.GroupContext == groupContext).ToListAsync());
        Assert.Empty(await _scenario.Admin.Context.ShareTargets
            .Where(t => t.ShareGroupId == bundle.GroupId).ToListAsync());
    }

    [Fact]
    public async Task DeleteExpiredInvitationsAsync_RemovesOnlyExpired()
    {
        var fresh = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, "Fresh");
        var stale = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, "Stale", ttl: TimeSpan.FromMilliseconds(1));

        // Force the stale row's ExpiresAt into the past.
        var staleRow = await _scenario.Admin.Context.Invitations
            .SingleAsync(i => i.SharingId == $"invitation-{stale.GroupId:N}:v1");
        staleRow.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await _scenario.Admin.Context.SaveChangesAsync();

        await _scenario.Admin.Invitations.DeleteExpiredInvitationsAsync();

        var remaining = await _scenario.Admin.Context.Invitations.ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("Fresh", remaining[0].Username);

        Assert.Empty(await _scenario.Admin.Context.ShareGroups
            .Where(g => g.GroupContext == $"invitation-{stale.GroupId:N}:v1").ToListAsync());
        Assert.NotEmpty(await _scenario.Admin.Context.ShareGroups
            .Where(g => g.GroupContext == $"invitation-{fresh.GroupId:N}:v1").ToListAsync());
    }

    // ----------------------------------------------------------------
    // Canonical-signing invariant — SignatureField is the highest [Key(N)]
    // ----------------------------------------------------------------

    [Fact]
    public void ContactAcceptancePayload_SignatureFieldIsHighestKey()
    {
        // Reflection lock: AcceptancePayloadSignature must hold the highest
        // [Key(N)] index on the type. A new field at a higher index would
        // silently shift canonical bytes and break verification of every
        // prior payload.
        var props = typeof(ContactAcceptancePayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => new
            {
                Prop = p,
                KeyAttr = p.GetCustomAttribute<KeyAttribute>()
            })
            .Where(x => x.KeyAttr is not null)
            .ToList();

        var maxKey = props.Max(x => x.KeyAttr!.IntKey);
        var signatureKey = props
            .Single(x => x.Prop.Name == nameof(ContactAcceptancePayload.AcceptancePayloadSignature))
            .KeyAttr!.IntKey;

        Assert.Equal(maxKey, signatureKey);
    }
}
