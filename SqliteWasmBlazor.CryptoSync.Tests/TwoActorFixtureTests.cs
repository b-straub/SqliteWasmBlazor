using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests against the <see cref="TwoActorBootstrap"/> fixture — verifies the
/// post-bootstrap state of a two-actor scenario (admin + one user) is exactly
/// what every subsequent integration scenario can rely on as its starting point.
/// </summary>
public class TwoActorFixtureTests : IAsyncLifetime
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
    // ADMIN STATE
    // ----------------------------------------------------------------

    [Fact]
    public async Task Admin_DeviceSettings_IsAdminFlag_True()
    {
        Assert.True(await _scenario.Admin.DeviceIdentity.IsAdminAsync());
    }

    [Fact]
    public async Task Admin_AdminContactId_LinkedToOwnRow()
    {
        var settings = await _scenario.Admin.DeviceIdentity.GetAsync();
        Assert.NotNull(settings);
        Assert.NotNull(settings.AdminContactId);

        var adminContact = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.Admin.Keys.Ed25519PublicKey);
        Assert.NotNull(adminContact);
        Assert.Equal(adminContact.Id, settings.AdminContactId);
    }

    [Fact]
    public async Task Admin_Contacts_ContainsBothActors()
    {
        var contacts = await _scenario.Admin.Contacts.GetAllAsync();
        Assert.Equal(2, contacts.Count);
        Assert.Contains(contacts, c => c.Ed25519PublicKey == _scenario.Admin.Keys.Ed25519PublicKey);
        Assert.Contains(contacts, c => c.Ed25519PublicKey == _scenario.User.Keys.Ed25519PublicKey);
    }

    [Fact]
    public async Task Admin_UserContact_IsFullTrustInSystemScope()
    {
        var userContact = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.User.Keys.Ed25519PublicKey);
        Assert.NotNull(userContact);
        Assert.Equal(ContactStatus.Verified, userContact.Status);
        Assert.Equal(SharingScope.PUBLIC, userContact.SharingScope);
        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, userContact.SharingId);
    }

    [Fact]
    public async Task Admin_HasSystemShareGroup_WithTwoShareTargets()
    {
        var group = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        Assert.Equal(1, group.KeyVersion);

        var targets = await _scenario.Admin.Context.ShareTargets
            .Where(t => t.ShareGroupId == group.Id)
            .ToListAsync();

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.MemberPublicKey == _scenario.Admin.Keys.X25519PublicKey
                                      && t.Role == SyncRole.OWNER);
        Assert.Contains(targets, t => t.MemberPublicKey == _scenario.User.Keys.X25519PublicKey
                                      && t.Role == SyncRole.VIEWER);
    }

    [Fact]
    public async Task Admin_GateAcceptsAdminAndUserAsFullTrustSenders()
    {
        var asAdmin = await _scenario.Admin.Gate.EnsureSenderTrustedAsync(_scenario.Admin.Keys.Ed25519PublicKey);
        var asUser = await _scenario.Admin.Gate.EnsureSenderTrustedAsync(_scenario.User.Keys.Ed25519PublicKey);
        Assert.Equal(ContactStatus.Verified, asAdmin.Status);
        Assert.Equal(ContactStatus.Verified, asUser.Status);
    }

    // ----------------------------------------------------------------
    // USER STATE (post-bootstrap delivery)
    // ----------------------------------------------------------------

    [Fact]
    public async Task User_DeviceSettings_IsAdminFlag_False()
    {
        Assert.False(await _scenario.User.DeviceIdentity.IsAdminAsync());
    }

    [Fact]
    public async Task User_AdminContactId_PointsAtAdminRow()
    {
        var settings = await _scenario.User.DeviceIdentity.GetAsync();
        Assert.NotNull(settings);
        Assert.NotNull(settings.AdminContactId);

        var adminContact = await _scenario.User.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.Admin.Keys.Ed25519PublicKey);
        Assert.NotNull(adminContact);
        Assert.Equal(adminContact.Id, settings.AdminContactId);
    }

    [Fact]
    public async Task User_Contacts_ContainsBothActors()
    {
        var contacts = await _scenario.User.Contacts.GetAllAsync();
        Assert.Equal(2, contacts.Count);
        Assert.Contains(contacts, c => c.Ed25519PublicKey == _scenario.Admin.Keys.Ed25519PublicKey);
        Assert.Contains(contacts, c => c.Ed25519PublicKey == _scenario.User.Keys.Ed25519PublicKey);
    }

    [Fact]
    public async Task User_PrimaryKeysAreShared_WithAdmin()
    {
        var adminContactOnAdmin = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.Admin.Keys.Ed25519PublicKey);
        var adminContactOnUser = await _scenario.User.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.Admin.Keys.Ed25519PublicKey);
        var userContactOnAdmin = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.User.Keys.Ed25519PublicKey);
        var userContactOnUser = await _scenario.User.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.User.Keys.Ed25519PublicKey);

        Assert.NotNull(adminContactOnAdmin);
        Assert.NotNull(adminContactOnUser);
        Assert.NotNull(userContactOnAdmin);
        Assert.NotNull(userContactOnUser);

        Assert.Equal(adminContactOnAdmin.Id, adminContactOnUser.Id);
        Assert.Equal(userContactOnAdmin.Id, userContactOnUser.Id);
    }

    [Fact]
    public async Task User_GateAcceptsAdminAsFullTrustSender()
    {
        var resolved = await _scenario.User.Gate.EnsureSenderTrustedAsync(_scenario.Admin.Keys.Ed25519PublicKey);
        Assert.Equal(ContactStatus.Verified, resolved.Status);
        Assert.Equal(_scenario.Admin.Keys.Ed25519PublicKey, resolved.Ed25519PublicKey);
    }

    [Fact]
    public async Task User_HasShareTargetForSystemScope_WithViewerRole()
    {
        // The user now has TWO ShareTargets: one for the system group
        // (Viewer role, wrapped by admin) and one for their own self-group
        // (Owner role, wrapped via self-ECDH on their own device).
        var systemGroup = await _scenario.User.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);

        var systemTarget = await _scenario.User.Context.ShareTargets
            .SingleAsync(t =>
                t.MemberPublicKey == _scenario.User.Keys.X25519PublicKey
                && t.ShareGroupId == systemGroup.Id);

        Assert.Equal(SyncRole.VIEWER, systemTarget.Role);
        Assert.NotEmpty(systemTarget.WrappedContentKey);
        Assert.True(systemTarget.WrappedContentKey.Length > 12);
    }

    [Fact]
    public async Task BothActors_AgreeOnSystemCek_ViaTheirOwnShareTargets()
    {
        // The deepest invariant: admin and user both unwrap their own
        // ShareTarget to obtain the SAME 32-byte system CEK.

        var systemGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);

        // Admin unwraps their self-ShareTarget
        var adminTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == _scenario.Admin.Keys.X25519PublicKey);
        var adminWrapped = CryptoSyncBootstrap.DeserializeWrappedCek(adminTarget.WrappedContentKey);
        var adminPrivKey = Convert.FromBase64String(_scenario.Admin.Keys.X25519PrivateKey);
        var adminWkResult = await _scenario.Crypto.DeriveWrappingKeyAsync(
            adminPrivKey, systemGroup.GroupAdminPublicKey, systemGroup.GroupContext);
        Assert.True(adminWkResult.Success);
        var adminCekResult = await _scenario.Crypto.UnwrapContentKeyAsync(adminWrapped, adminWkResult.Value!);
        Assert.True(adminCekResult.Success);

        // User unwraps their ShareTarget
        var userGroup = await _scenario.User.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        var userTarget = await _scenario.User.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == userGroup.Id
                && t.MemberPublicKey == _scenario.User.Keys.X25519PublicKey);
        var userWrapped = CryptoSyncBootstrap.DeserializeWrappedCek(userTarget.WrappedContentKey);
        var userPrivKey = Convert.FromBase64String(_scenario.User.Keys.X25519PrivateKey);
        var userWkResult = await _scenario.Crypto.DeriveWrappingKeyAsync(
            userPrivKey, userGroup.GroupAdminPublicKey, userGroup.GroupContext);
        Assert.True(userWkResult.Success);
        var userCekResult = await _scenario.Crypto.UnwrapContentKeyAsync(userWrapped, userWkResult.Value!);
        Assert.True(userCekResult.Success);

        Assert.Equal(adminCekResult.Value!.ToArray(), userCekResult.Value!.ToArray());
    }
}
