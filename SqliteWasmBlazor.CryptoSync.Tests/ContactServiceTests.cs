using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for the surviving <see cref="ContactService"/> read/admin helpers.
/// Contact creation + trust establishment moved to
/// <see cref="ContactInvitationService"/> — those tests live in
/// <c>ContactInvitationServiceTests</c>.
/// </summary>
public class ContactServiceTests : IAsyncLifetime
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

    [Fact]
    public async Task GetByEd25519PublicKey_ReturnsMatchingContact()
    {
        var found = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.User.Keys.Ed25519PublicKey);

        Assert.NotNull(found);
        Assert.Equal(_scenario.User.Keys.Ed25519PublicKey, found.Ed25519PublicKey);
    }

    [Fact]
    public async Task GetByEd25519PublicKey_ReturnsNull_ForUnknownKey()
    {
        var found = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync("unknown-key");

        Assert.Null(found);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAdminAndUser()
    {
        var all = await _scenario.Admin.Contacts.GetAllAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetRecipientPublicKeys_ReturnsBothActorX25519Keys()
    {
        var keys = await _scenario.Admin.Contacts.GetRecipientPublicKeysAsync();

        Assert.Equal(2, keys.Length);
        Assert.Contains(_scenario.Admin.Keys.X25519PublicKey, keys);
        Assert.Contains(_scenario.User.Keys.X25519PublicKey, keys);
    }

    [Fact]
    public async Task Delete_SoftDeletesContact()
    {
        var userContact = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.User.Keys.Ed25519PublicKey);
        Assert.NotNull(userContact);

        await _scenario.Admin.Contacts.DeleteAsync(userContact.Id);

        var all = await _scenario.Admin.Contacts.GetAllAsync();
        Assert.Single(all); // admin remains visible

        var raw = await _scenario.Admin.Context.Contacts
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(c => c.Id == userContact.Id);
        Assert.NotNull(raw);
        Assert.True(raw.IsDeleted);
        Assert.NotNull(raw.DeletedAt);
    }

    [Fact]
    public async Task Delete_NonExistentContact_NoOp()
    {
        await _scenario.Admin.Contacts.DeleteAsync(Guid.NewGuid());
    }

    // ----------------------------------------------------------------
    // RevokeContactAsync — system-admin revocation flow
    // ----------------------------------------------------------------

    [Fact]
    public async Task RevokeContactAsync_RotatesGroupsSoftDeletesAndPushesWhitelistRevoke()
    {
        var userContact = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.User.Keys.Ed25519PublicKey);
        Assert.NotNull(userContact);

        // System-group key version pre-revoke.
        var systemGroupBefore = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext.StartsWith("system:") || g.GroupContext == "system:v1");
        var keyVersionBefore = systemGroupBefore.KeyVersion;

        // Push count from the bootstrap (Create v=1, Promote v=2).
        var pushesBefore = _scenario.Admin.WhitelistPush.Pushes.Count;

        await _scenario.Admin.Contacts.RevokeContactAsync(
            userContact.Id, _scenario.Admin.Keys, InvitationTestSalt.Default);

        // (1) Crypto-layer rotation: system group's key version advanced.
        var systemGroupAfter = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.Id == systemGroupBefore.Id);
        Assert.True(systemGroupAfter.KeyVersion > keyVersionBefore);

        // (2) Soft-delete: contact no longer visible via filtered query.
        Assert.Null(await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.User.Keys.Ed25519PublicKey));
        var raw = await _scenario.Admin.Context.Contacts
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == userContact.Id);
        Assert.True(raw.IsDeleted);

        // (3) Whitelist push: Revoke op for the contact's Ed25519 hash, at v=3.
        var pushes = _scenario.Admin.WhitelistPush.Pushes;
        Assert.Equal(pushesBefore + 1, pushes.Count);
        var revokePush = pushes[^1];
        Assert.Equal(3L, revokePush.Version);
        var op = Assert.Single(revokePush.Operations);
        var revoke = Assert.IsType<WhitelistOp.RevokeOp>(op);

        var expectedHash = WhitelistPushService.HashPubkey(
            InvitationTestSalt.Default, userContact.Ed25519PublicKey);
        Assert.Equal(expectedHash, revoke.PubkeyHash);
        Assert.True(revoke.RevokedAt > 0);

        var state = await _scenario.Admin.Context.SyncStates
            .SingleAsync(s => s.Id == SyncState.EngineCursorId);
        Assert.Equal(3L, state.LastWhitelistVersion);
    }

    [Fact]
    public async Task RevokeContactAsync_AdminContact_Throws()
    {
        var adminContact = await _scenario.Admin.Context.Contacts
            .SingleAsync(c => c.IsAdmin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _scenario.Admin.Contacts.RevokeContactAsync(
                adminContact.Id, _scenario.Admin.Keys, InvitationTestSalt.Default).AsTask());
    }

    [Fact]
    public async Task RevokeContactAsync_UnknownContactId_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _scenario.Admin.Contacts.RevokeContactAsync(
                Guid.NewGuid(), _scenario.Admin.Keys, InvitationTestSalt.Default).AsTask());
    }
}
