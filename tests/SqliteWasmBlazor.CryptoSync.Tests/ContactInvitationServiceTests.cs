using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for <see cref="ContactInvitationService"/>: admin-initiated
/// invitation channel construction, response ingest, and promotion to a
/// real <see cref="TrustedContact"/>.
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
    // CreateInvitationAsync — admin-initiated invitation channel
    // ----------------------------------------------------------------

    [Fact]
    public async Task CreateInvitationAsync_PersistsShareGroupAndTwoShareTargets()
    {
        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, InvitationTestSalt.Default, "Bob", "bob@test.com");

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
        Assert.Single(targets, t => t.MemberPublicKey != _scenario.Admin.Keys.X25519PublicKey);
    }

    [Fact]
    public async Task CreateInvitationAsync_PersistsInvitationRow_PendingState()
    {
        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, InvitationTestSalt.Default, "Bob", "bob@test.com", "Bob from accounting");

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
            _scenario.Admin.Keys, InvitationTestSalt.Default, "Bob");

        var derived = await _scenario.Crypto.DeriveDualKeyPairAsync(bundle.TransportSecret);

        var groupContext = $"invitation-{bundle.GroupId:N}:v1";
        var group = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == groupContext);
        Assert.Contains(_scenario.Admin.Context.ShareTargets,
            t => t.ShareGroupId == group.Id && t.MemberPublicKey == derived.X25519PublicKey);
    }

    [Fact]
    public async Task CreateInvitationAsync_BundleSignatureVerifiesAgainstAdminPubKey()
    {
        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, InvitationTestSalt.Default, "Bob");

        var derived = await _scenario.Crypto.DeriveDualKeyPairAsync(bundle.TransportSecret);
        var canonical = ContactInvitationService.BuildBundleCanonical(
            derived.X25519PublicKey, bundle.GroupId, bundle.ExpiresAt);

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
            _scenario.Admin.Keys, InvitationTestSalt.Default, "Bob");
        var after = DateTime.UtcNow;

        var minExpected = before + TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1);
        var maxExpected = after + TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1);
        Assert.InRange(bundle.ExpiresAt, minExpected, maxExpected);
    }

    [Fact]
    public async Task CreateInvitationAsync_PushesTransportEd25519HashToWhitelist()
    {
        // The recording push service captures the exact ops the hook emits.
        // CreateInvitation must push exactly one Add op for the transport's
        // Ed25519 hash, advancing LastWhitelistVersion by 1.
        var pushesBefore = _scenario.Admin.WhitelistPush.Pushes.Count;
        var versionBefore = (await _scenario.Admin.Context.SyncStates
            .FirstOrDefaultAsync(s => s.Id == SyncState.EngineCursorId))?.LastWhitelistVersion ?? 0;

        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, InvitationTestSalt.Default, "Bob");

        var pushes = _scenario.Admin.WhitelistPush.Pushes;
        Assert.Equal(pushesBefore + 1, pushes.Count);
        var lastPush = pushes[^1];
        Assert.Equal(versionBefore + 1, lastPush.Version);
        var op = Assert.Single(lastPush.Operations);
        var add = Assert.IsType<WhitelistOp.AddOp>(op);

        // The hash is sha256(salt || transport.Ed25519PublicKey).
        var transportDual = await _scenario.Crypto.DeriveDualKeyPairAsync(bundle.TransportSecret);
        var expectedHash = WhitelistPushService.HashPubkey(
            InvitationTestSalt.Default, transportDual.Ed25519PublicKey);
        Assert.Equal(expectedHash, add.PubkeyHash);

        // SyncState.LastWhitelistVersion advanced.
        var state = await _scenario.Admin.Context.SyncStates
            .SingleAsync(s => s.Id == SyncState.EngineCursorId);
        Assert.Equal(versionBefore + 1, state.LastWhitelistVersion);
    }

    [Fact]
    public async Task CreateInvitationAsync_BlankUsername_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _scenario.Admin.Invitations.CreateInvitationAsync(_scenario.Admin.Keys, InvitationTestSalt.Default, "  ").AsTask());
    }

    [Fact]
    public async Task RevokeInvitationAsync_DeletesShareGroupAndRow()
    {
        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, InvitationTestSalt.Default, "Bob");
        var groupContext = $"invitation-{bundle.GroupId:N}:v1";
        var invitationId = (await _scenario.Admin.Context.Invitations
            .SingleAsync(i => i.SharingId == groupContext)).Id;

        await _scenario.Admin.Invitations.RevokeInvitationAsync(
            invitationId, _scenario.Admin.Keys, InvitationTestSalt.Default);

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
            _scenario.Admin.Keys, InvitationTestSalt.Default, "Fresh");
        var stale = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, InvitationTestSalt.Default, "Stale", ttl: TimeSpan.FromMilliseconds(1));

        var staleRow = await _scenario.Admin.Context.Invitations
            .SingleAsync(i => i.SharingId == $"invitation-{stale.GroupId:N}:v1");
        staleRow.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await _scenario.Admin.Context.SaveChangesAsync();

        await _scenario.Admin.Invitations.DeleteExpiredInvitationsAsync(
            _scenario.Admin.Keys, InvitationTestSalt.Default);

        var remaining = await _scenario.Admin.Context.Invitations.ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("Fresh", remaining[0].Username);

        Assert.Empty(await _scenario.Admin.Context.ShareGroups
            .Where(g => g.GroupContext == $"invitation-{stale.GroupId:N}:v1").ToListAsync());
        Assert.NotEmpty(await _scenario.Admin.Context.ShareGroups
            .Where(g => g.GroupContext == $"invitation-{fresh.GroupId:N}:v1").ToListAsync());
    }

    // ----------------------------------------------------------------
    // Post-bootstrap state — TwoActorBootstrap exercises the full pipeline.
    // The fixture already completed CreateInvitation → Respond → Ingest → Promote.
    // ----------------------------------------------------------------

    [Fact]
    public async Task Promote_PersistsTrustedContact_PUBLICScope()
    {
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
    }

    [Fact]
    public async Task Promote_SelfGroupRowsRouteViaSystem()
    {
        // Contact's self-group ShareGroup + ShareTarget must carry
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

    [Fact]
    public async Task Promote_AdminCannotUnwrapContactSelfGroupCek()
    {
        var userSelfGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext.StartsWith("self-")
                && g.GroupAdminPublicKey == _scenario.User.Keys.X25519PublicKey);
        var userSelfTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == userSelfGroup.Id);

        var wrapped = CryptoSyncBootstrap.DeserializeWrappedCek(userSelfTarget.WrappedContentKey);

        var adminPriv = _scenario.Admin.Keys.X25519PrivateKey;
        var wrongWk = await _scenario.Crypto.DeriveWrappingKeyAsync(
            adminPriv,
            _scenario.User.Keys.X25519PublicKey,
            userSelfGroup.GroupContext);
        Assert.True(wrongWk.Success);

        var cekResult = await _scenario.Crypto.UnwrapContentKeyAsync(wrapped, wrongWk.Value);
        Assert.False(cekResult.Success);
    }

    [Fact]
    public async Task Promote_ContactCanUnwrapOwnSelfGroupCek()
    {
        var userSelfGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext.StartsWith("self-")
                && g.GroupAdminPublicKey == _scenario.User.Keys.X25519PublicKey);
        var userSelfTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == userSelfGroup.Id);

        var wrapped = CryptoSyncBootstrap.DeserializeWrappedCek(userSelfTarget.WrappedContentKey);
        var userPriv = _scenario.User.Keys.X25519PrivateKey;
        var wk = await _scenario.Crypto.DeriveWrappingKeyAsync(
            userPriv,
            _scenario.User.Keys.X25519PublicKey,
            userSelfGroup.GroupContext);
        Assert.True(wk.Success);

        var cek = await _scenario.Crypto.UnwrapContentKeyAsync(wrapped, wk.Value);
        Assert.True(cek.Success);
        Assert.Equal(32, cek.Value.Length);
    }

    [Fact]
    public async Task Promote_DeletesInvitationChannelRows()
    {
        // After promotion, no Invitation rows or invitation-* ShareGroups remain.
        Assert.Empty(await _scenario.Admin.Context.Invitations.ToListAsync());
        Assert.Empty(await _scenario.Admin.Context.ShareGroups
            .Where(g => g.GroupContext.StartsWith("invitation-")).ToListAsync());
    }

    [Fact]
    public async Task Promote_PushesRevokeTransportPlusAddContactInOneVersion()
    {
        // After full bootstrap (TwoActorBootstrap already exercised the
        // happy-path Create+Respond+Ingest+Promote), the recording service
        // captured TWO pushes: v1 = Add(transport hash) from CreateInvitation,
        // v2 = Revoke(transport)+Add(contact) from PromoteInvitation.
        var pushes = _scenario.Admin.WhitelistPush.Pushes;
        Assert.Equal(2, pushes.Count);

        var promotePush = pushes[1];
        Assert.Equal(2L, promotePush.Version);
        Assert.Equal(2, promotePush.Operations.Count);

        var revoke = Assert.IsType<WhitelistOp.RevokeOp>(promotePush.Operations[0]);
        var add = Assert.IsType<WhitelistOp.AddOp>(promotePush.Operations[1]);

        // Revoke targets the transport's Ed25519 hash (same one Create added).
        Assert.Equal(pushes[0].Operations[0].PubkeyHash, revoke.PubkeyHash);
        Assert.True(revoke.RevokedAt > 0);

        // Add targets the user's real Ed25519 hash.
        var expectedContactHash = WhitelistPushService.HashPubkey(
            InvitationTestSalt.Default, _scenario.User.Keys.Ed25519PublicKey);
        Assert.Equal(expectedContactHash, add.PubkeyHash);

        var state = await _scenario.Admin.Context.SyncStates
            .SingleAsync(s => s.Id == SyncState.EngineCursorId);
        Assert.Equal(2L, state.LastWhitelistVersion);
    }

    // ----------------------------------------------------------------
    // PromoteInvitationAsync — negative paths
    // ----------------------------------------------------------------

    [Fact]
    public async Task Promote_UnknownInvitationId_Throws()
    {
        await Assert.ThrowsAsync<InvitationNotFoundException>(
            () => _scenario.Admin.Invitations.PromoteInvitationAsync(
                Guid.NewGuid(), _scenario.Admin.Keys, InvitationTestSalt.Default).AsTask());
    }

    [Fact]
    public async Task Promote_NotResponded_Throws()
    {
        // Create a fresh invitation but don't respond.
        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, InvitationTestSalt.Default, "Bob");

        await Assert.ThrowsAsync<InvitationNotRespondedException>(
            () => _scenario.Admin.Invitations.PromoteInvitationAsync(
                bundle.GroupId, _scenario.Admin.Keys, InvitationTestSalt.Default).AsTask());
    }

    [Fact]
    public async Task Promote_ExpiredInvitation_Throws()
    {
        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, InvitationTestSalt.Default, "Carol");

        // Force expiry into the past in the local DB without going through SaveChanges
        // (the row's UpdatedAt would otherwise be touched by the interceptor).
        var row = await _scenario.Admin.Context.Invitations
            .SingleAsync(i => i.Id == bundle.GroupId);
        row.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await _scenario.Admin.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvitationExpiredException>(
            () => _scenario.Admin.Invitations.PromoteInvitationAsync(
                bundle.GroupId, _scenario.Admin.Keys, InvitationTestSalt.Default).AsTask());
    }

    [Fact]
    public async Task Promote_DoubleCall_SecondThrowsNotFound()
    {
        var newbie = await TestActor.CreateAsync("Dave", isAdmin: false, seedByte: 220, _scenario.Crypto);
        try
        {
            var relay = new InMemorySyncRelay();
            var adminTransport = new InMemorySyncTransport(relay);
            var contactTransport = new InMemorySyncTransport(relay);

            var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(_scenario.Admin.Keys, InvitationTestSalt.Default, "Dave");
            await newbie.Invitations.RespondToInvitationAsync(
                bundle, newbie.Keys,
                new ContactUserData { Username = "Dave", Email = "dave@test.com" },
                contactTransport);
            await _scenario.Admin.Invitations.IngestInvitationResponsesAsync(_scenario.Admin.Keys, adminTransport);

            await _scenario.Admin.Invitations.PromoteInvitationAsync(bundle.GroupId, _scenario.Admin.Keys, InvitationTestSalt.Default);

            await Assert.ThrowsAsync<InvitationNotFoundException>(
                () => _scenario.Admin.Invitations.PromoteInvitationAsync(
                    bundle.GroupId, _scenario.Admin.Keys, InvitationTestSalt.Default).AsTask());
        }
        finally
        {
            await newbie.DisposeAsync();
        }
    }

    [Fact]
    public async Task Promote_TamperedSignature_Throws()
    {
        var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
            _scenario.Admin.Keys, InvitationTestSalt.Default, "Eve");

        var relay = new InMemorySyncRelay();
        var adminTransport = new InMemorySyncTransport(relay);
        var contactTransport = new InMemorySyncTransport(relay);

        // Make the bundle reusable from the user actor (different keys).
        var userCrypto = _scenario.Crypto;
        var freshNewbie = await TestActor.CreateAsync("Eve", isAdmin: false, seedByte: 230, userCrypto);
        try
        {
            var freshContactTransport = new InMemorySyncTransport(relay);
            await freshNewbie.Invitations.RespondToInvitationAsync(
                bundle, freshNewbie.Keys,
                new ContactUserData { Username = "Eve", Email = "eve@test.com" },
                freshContactTransport);

            await _scenario.Admin.Invitations.IngestInvitationResponsesAsync(
                _scenario.Admin.Keys, adminTransport);

            // Tamper with the row's ContactSignature post-ingest.
            var row = await _scenario.Admin.Context.Invitations
                .SingleAsync(i => i.Id == bundle.GroupId);
            row.ContactSignature![0] ^= 0xFF;
            await _scenario.Admin.Context.SaveChangesAsync();

            await Assert.ThrowsAsync<InvalidInvitationResponseException>(
                () => _scenario.Admin.Invitations.PromoteInvitationAsync(
                    bundle.GroupId, _scenario.Admin.Keys, InvitationTestSalt.Default).AsTask());
        }
        finally
        {
            await freshNewbie.DisposeAsync();
        }
    }

    // ----------------------------------------------------------------
    // IngestInvitationResponsesAsync
    // ----------------------------------------------------------------

    [Fact]
    public async Task Ingest_NoEnvelopes_ReturnsZero()
    {
        var relay = new InMemorySyncRelay();
        var adminTransport = new InMemorySyncTransport(relay);

        var ingested = await _scenario.Admin.Invitations.IngestInvitationResponsesAsync(
            _scenario.Admin.Keys, adminTransport);

        Assert.Equal(0, ingested);
    }

    [Fact]
    public async Task Ingest_FillsRowWithContactPubkeys()
    {
        var newbie = await TestActor.CreateAsync("Frank", isAdmin: false, seedByte: 240, _scenario.Crypto);
        try
        {
            var relay = new InMemorySyncRelay();
            var adminTransport = new InMemorySyncTransport(relay);
            var contactTransport = new InMemorySyncTransport(relay);

            var bundle = await _scenario.Admin.Invitations.CreateInvitationAsync(
                _scenario.Admin.Keys, InvitationTestSalt.Default, "Frank");
            await newbie.Invitations.RespondToInvitationAsync(
                bundle, newbie.Keys,
                new ContactUserData { Username = "Frank", Email = "frank@test.com" },
                contactTransport);

            var ingested = await _scenario.Admin.Invitations.IngestInvitationResponsesAsync(
                _scenario.Admin.Keys, adminTransport);
            Assert.Equal(1, ingested);

            var row = await _scenario.Admin.Context.Invitations.SingleAsync(i => i.Id == bundle.GroupId);
            Assert.Equal(newbie.Keys.X25519PublicKey, row.ContactX25519PublicKey);
            Assert.Equal(newbie.Keys.Ed25519PublicKey, row.ContactEd25519PublicKey);
            Assert.NotNull(row.ContactSignature);
            Assert.NotNull(row.SelfGroupId);
            Assert.NotNull(row.SelfWrappedContentKey);
        }
        finally
        {
            await newbie.DisposeAsync();
        }
    }
}
