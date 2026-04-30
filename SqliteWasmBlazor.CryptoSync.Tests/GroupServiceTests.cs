using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for <see cref="GroupService"/> — ShareGroup + ShareTarget CRUD
/// with real crypto (BouncyCastle). Each test starts from the two-actor
/// bootstrap (admin + user with system scope already wired).
/// </summary>
public class GroupServiceTests : IAsyncLifetime
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

    private ReadOnlyMemory<byte> AdminPrivateKey =>
        Convert.FromBase64String(_scenario.Admin.Keys.X25519PrivateKey);

    // ----------------------------------------------------------------
    // CREATE GROUP
    // ----------------------------------------------------------------

    [Fact]
    public async Task CreateGroup_CreatesGroupWithMembers()
    {
        var adminContact = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.Admin.Keys.Ed25519PublicKey);

        var group = await _scenario.Admin.Groups.CreateGroupAsync(
            _scenario.Admin.Keys,
            [
                (_scenario.Admin.Keys.X25519PublicKey, SyncRole.OWNER, adminContact!.Id),
                (_scenario.User.Keys.X25519PublicKey, SyncRole.EDITOR, adminContact.Id)
            ],
            "shopping-list-1:v1");

        Assert.Equal("shopping-list-1:v1", group.GroupContext);
        Assert.Equal(1, group.KeyVersion);
        Assert.Equal(_scenario.Admin.Keys.X25519PublicKey, group.GroupAdminPublicKey);

        var members = await _scenario.Admin.Groups.GetMembersAsync(group.Id);
        Assert.Equal(2, members.Count);
        Assert.Contains(members, m => m.MemberPublicKey == _scenario.Admin.Keys.X25519PublicKey
                                      && m.Role == SyncRole.OWNER);
        Assert.Contains(members, m => m.MemberPublicKey == _scenario.User.Keys.X25519PublicKey
                                      && m.Role == SyncRole.EDITOR);
    }

    [Fact]
    public async Task CreateGroup_MembersCanUnwrapSameCek()
    {
        var adminContact = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.Admin.Keys.Ed25519PublicKey);

        var group = await _scenario.Admin.Groups.CreateGroupAsync(
            _scenario.Admin.Keys,
            [
                (_scenario.Admin.Keys.X25519PublicKey, SyncRole.OWNER, adminContact!.Id),
                (_scenario.User.Keys.X25519PublicKey, SyncRole.EDITOR, adminContact.Id)
            ],
            "cek-test:v1");

        var targets = await _scenario.Admin.Groups.GetMembersAsync(group.Id);
        var adminTarget = targets.Single(t => t.MemberPublicKey == _scenario.Admin.Keys.X25519PublicKey);
        var userTarget = targets.Single(t => t.MemberPublicKey == _scenario.User.Keys.X25519PublicKey);

        // Admin unwraps
        var adminWrapped = CryptoSyncBootstrap.DeserializeWrappedCek(adminTarget.WrappedContentKey);
        var adminWk = await _scenario.Crypto.DeriveWrappingKeyAsync(
            AdminPrivateKey, group.GroupAdminPublicKey, group.GroupContext);
        Assert.True(adminWk.Success);
        var adminCek = await _scenario.Crypto.UnwrapContentKeyAsync(adminWrapped, adminWk.Value!);
        Assert.True(adminCek.Success);

        // User unwraps
        var userWrapped = CryptoSyncBootstrap.DeserializeWrappedCek(userTarget.WrappedContentKey);
        var userPrivKey = Convert.FromBase64String(_scenario.User.Keys.X25519PrivateKey);
        var userWk = await _scenario.Crypto.DeriveWrappingKeyAsync(
            userPrivKey, group.GroupAdminPublicKey, group.GroupContext);
        Assert.True(userWk.Success);
        var userCek = await _scenario.Crypto.UnwrapContentKeyAsync(userWrapped, userWk.Value!);
        Assert.True(userCek.Success);

        Assert.Equal(adminCek.Value!.ToArray(), userCek.Value!.ToArray());
    }

    // ----------------------------------------------------------------
    // ADD MEMBERS
    // ----------------------------------------------------------------

    [Fact]
    public async Task AddMembers_WrapsExistingCekForNewMember()
    {
        // System group already has admin + user from bootstrap
        var systemGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);

        // Create a third actor
        var crypto = _scenario.Crypto;
        await using var thirdActor = await TestActor.CreateAsync("Charlie", false, 200, crypto);

        // Add Charlie as contact via direct insert (the full invitation
        // flow is exercised in ContactInvitationServiceTests).
        var charlieContact = new TrustedContact
        {
            Id = Guid.NewGuid(),
            Username = "Charlie",
            Email = "charlie@test.com",
            X25519PublicKey = thirdActor.Keys.X25519PublicKey,
            Ed25519PublicKey = thirdActor.Keys.Ed25519PublicKey,
            IsAdmin = false,
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.PUBLIC,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        };
        _scenario.Admin.Context.Contacts.Add(charlieContact);
        await _scenario.Admin.Context.SaveChangesAsync();

        var adminContact = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.Admin.Keys.Ed25519PublicKey);

        await _scenario.Admin.Groups.AddMembersAsync(
            systemGroup.Id,
            _scenario.Admin.Keys,
            [(thirdActor.Keys.X25519PublicKey, SyncRole.VIEWER, adminContact!.Id)]);

        var members = await _scenario.Admin.Groups.GetMembersAsync(systemGroup.Id);
        Assert.Equal(3, members.Count);
        Assert.Contains(members, m => m.MemberPublicKey == thirdActor.Keys.X25519PublicKey
                                      && m.Role == SyncRole.VIEWER);
    }

    // ----------------------------------------------------------------
    // REMOVE MEMBER (key rotation)
    // ----------------------------------------------------------------

    [Fact]
    public async Task RemoveMember_RotatesKeyAndExcludesRemovedMember()
    {
        var adminContact = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.Admin.Keys.Ed25519PublicKey);

        // Create a group with admin + user + third actor
        var crypto = _scenario.Crypto;
        await using var charlie = await TestActor.CreateAsync("Charlie", false, 200, crypto);

        var group = await _scenario.Admin.Groups.CreateGroupAsync(
            _scenario.Admin.Keys,
            [
                (_scenario.Admin.Keys.X25519PublicKey, SyncRole.OWNER, adminContact!.Id),
                (_scenario.User.Keys.X25519PublicKey, SyncRole.EDITOR, adminContact.Id),
                (charlie.Keys.X25519PublicKey, SyncRole.VIEWER, adminContact.Id)
            ],
            "rotate-test:v1");

        Assert.Equal(1, group.KeyVersion);

        // Remove Charlie
        var newVersion = await _scenario.Admin.Groups.RemoveMemberAsync(
            group.Id, _scenario.Admin.Keys, charlie.Keys.X25519PublicKey);

        Assert.Equal(2, newVersion);

        // Reload group
        await _scenario.Admin.Context.Entry(group).ReloadAsync();
        Assert.Equal(2, group.KeyVersion);
        Assert.Equal("rotate-test:v2", group.GroupContext);

        // Current members: only admin + user at v2
        var currentMembers = await _scenario.Admin.Groups.GetMembersAsync(group.Id);
        Assert.Equal(2, currentMembers.Count);
        Assert.DoesNotContain(currentMembers, m => m.MemberPublicKey == charlie.Keys.X25519PublicKey);

        // Old v1 targets still exist (for historical decryption)
        var allTargets = await _scenario.Admin.Context.ShareTargets
            .Where(t => t.ShareGroupId == group.Id)
            .ToListAsync();
        Assert.Equal(5, allTargets.Count); // 3 at v1 + 2 at v2
    }

    [Fact]
    public async Task RemoveMember_OldCekDiffersFromNewCek()
    {
        var adminContact = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.Admin.Keys.Ed25519PublicKey);

        var group = await _scenario.Admin.Groups.CreateGroupAsync(
            _scenario.Admin.Keys,
            [
                (_scenario.Admin.Keys.X25519PublicKey, SyncRole.OWNER, adminContact!.Id),
                (_scenario.User.Keys.X25519PublicKey, SyncRole.EDITOR, adminContact.Id)
            ],
            "cek-rotate:v1");

        // Get v1 CEK
        var v1Target = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == group.Id
                && t.MemberPublicKey == _scenario.Admin.Keys.X25519PublicKey);
        var v1Wrapped = CryptoSyncBootstrap.DeserializeWrappedCek(v1Target.WrappedContentKey);
        var v1Wk = await _scenario.Crypto.DeriveWrappingKeyAsync(
            AdminPrivateKey, group.GroupAdminPublicKey, "cek-rotate:v1");
        var v1Cek = await _scenario.Crypto.UnwrapContentKeyAsync(v1Wrapped, v1Wk.Value!);

        // Remove user → rotate
        await _scenario.Admin.Groups.RemoveMemberAsync(
            group.Id, _scenario.Admin.Keys, _scenario.User.Keys.X25519PublicKey);

        // Get v2 CEK
        await _scenario.Admin.Context.Entry(group).ReloadAsync();
        var v2Target = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == group.Id
                && t.MemberPublicKey == _scenario.Admin.Keys.X25519PublicKey
                && t.KeyVersion == 2);
        var v2Wrapped = CryptoSyncBootstrap.DeserializeWrappedCek(v2Target.WrappedContentKey);
        var v2Wk = await _scenario.Crypto.DeriveWrappingKeyAsync(
            AdminPrivateKey, group.GroupAdminPublicKey, group.GroupContext);
        var v2Cek = await _scenario.Crypto.UnwrapContentKeyAsync(v2Wrapped, v2Wk.Value!);

        // CEKs must differ (rotation generated a new random key)
        Assert.NotEqual(v1Cek.Value!.ToArray(), v2Cek.Value!.ToArray());
    }

    [Fact]
    public async Task RemoveMember_NonExistentMember_Throws()
    {
        var systemGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _scenario.Admin.Groups.RemoveMemberAsync(
                systemGroup.Id, _scenario.Admin.Keys, "nonexistent-key").AsTask());
    }

    // ----------------------------------------------------------------
    // UPDATE ROLE
    // ----------------------------------------------------------------

    [Fact]
    public async Task UpdateMemberRole_ChangesRole()
    {
        var systemGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);

        await _scenario.Admin.Groups.UpdateMemberRoleAsync(
            systemGroup.Id, _scenario.Admin.Keys,
            _scenario.User.Keys.X25519PublicKey, SyncRole.EDITOR);

        var members = await _scenario.Admin.Groups.GetMembersAsync(systemGroup.Id);
        var userTarget = members.Single(m => m.MemberPublicKey == _scenario.User.Keys.X25519PublicKey);
        Assert.Equal(SyncRole.EDITOR, userTarget.Role);

        var signer = new DeclarationSigner(_scenario.Crypto);
        var ok = await signer.VerifyShareTargetAsync(
            userTarget.GroupAdminEd25519PublicKey,
            userTarget.MemberPublicKey,
            userTarget.Role,
            systemGroup.GroupContext,
            userTarget.KeyVersion,
            userTarget.AdminSignature);
        Assert.True(ok, "Updated role must carry a freshly valid ShareTarget credential");
    }

    // ----------------------------------------------------------------
    // SHARETARGET CREDENTIAL SIGNATURES
    //
    // Every ShareTarget written by GroupService must carry an AdminSignature
    // verifiable against the admin's Ed25519 public key. The worker's
    // resolveSenderPermissions hard-rejects ShareTargets with empty
    // AdminSignature (crypto-ops.ts:471-481), so missing signatures here
    // would silently break sync after rotation.
    // ----------------------------------------------------------------

    [Fact]
    public async Task CreateGroup_NewTargetsCarryValidAdminSignature()
    {
        var adminContact = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.Admin.Keys.Ed25519PublicKey);

        var group = await _scenario.Admin.Groups.CreateGroupAsync(
            _scenario.Admin.Keys,
            [
                (_scenario.Admin.Keys.X25519PublicKey, SyncRole.OWNER, adminContact!.Id),
                (_scenario.User.Keys.X25519PublicKey, SyncRole.EDITOR, adminContact.Id)
            ],
            "sig-create:v1");

        var signer = new DeclarationSigner(_scenario.Crypto);
        var members = await _scenario.Admin.Groups.GetMembersAsync(group.Id);

        Assert.Equal(2, members.Count);
        foreach (var target in members)
        {
            Assert.NotEmpty(target.AdminSignature);
            Assert.Equal(_scenario.Admin.Keys.Ed25519PublicKey, target.GroupAdminEd25519PublicKey);

            var ok = await signer.VerifyShareTargetAsync(
                target.GroupAdminEd25519PublicKey,
                target.MemberPublicKey,
                target.Role,
                group.GroupContext,
                target.KeyVersion,
                target.AdminSignature);
            Assert.True(ok, $"AdminSignature on member {target.MemberPublicKey} did not verify");
        }
    }

    [Fact]
    public async Task AddMembers_NewTargetsCarryValidAdminSignature()
    {
        var systemGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);

        await using var charlie = await TestActor.CreateAsync("Charlie", false, 200, _scenario.Crypto);
        var charlieContact = new TrustedContact
        {
            Id = Guid.NewGuid(),
            Username = "Charlie",
            Email = "charlie@test.com",
            X25519PublicKey = charlie.Keys.X25519PublicKey,
            Ed25519PublicKey = charlie.Keys.Ed25519PublicKey,
            IsAdmin = false,
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.PUBLIC,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        };
        _scenario.Admin.Context.Contacts.Add(charlieContact);
        await _scenario.Admin.Context.SaveChangesAsync();

        var adminContact = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.Admin.Keys.Ed25519PublicKey);

        await _scenario.Admin.Groups.AddMembersAsync(
            systemGroup.Id,
            _scenario.Admin.Keys,
            [(charlie.Keys.X25519PublicKey, SyncRole.VIEWER, adminContact!.Id)]);

        var charlieTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == charlie.Keys.X25519PublicKey
                && t.KeyVersion == systemGroup.KeyVersion);

        Assert.NotEmpty(charlieTarget.AdminSignature);
        Assert.Equal(_scenario.Admin.Keys.Ed25519PublicKey, charlieTarget.GroupAdminEd25519PublicKey);

        var signer = new DeclarationSigner(_scenario.Crypto);
        var ok = await signer.VerifyShareTargetAsync(
            charlieTarget.GroupAdminEd25519PublicKey,
            charlieTarget.MemberPublicKey,
            charlieTarget.Role,
            systemGroup.GroupContext,
            charlieTarget.KeyVersion,
            charlieTarget.AdminSignature);
        Assert.True(ok);
    }

    [Fact]
    public async Task RemoveMember_NewTargetsCarryValidAdminSignature()
    {
        var adminContact = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.Admin.Keys.Ed25519PublicKey);
        await using var charlie = await TestActor.CreateAsync("Charlie", false, 200, _scenario.Crypto);

        var group = await _scenario.Admin.Groups.CreateGroupAsync(
            _scenario.Admin.Keys,
            [
                (_scenario.Admin.Keys.X25519PublicKey, SyncRole.OWNER, adminContact!.Id),
                (_scenario.User.Keys.X25519PublicKey, SyncRole.EDITOR, adminContact.Id),
                (charlie.Keys.X25519PublicKey, SyncRole.VIEWER, adminContact.Id)
            ],
            "sig-rotate:v1");

        var newVersion = await _scenario.Admin.Groups.RemoveMemberAsync(
            group.Id, _scenario.Admin.Keys, charlie.Keys.X25519PublicKey);

        await _scenario.Admin.Context.Entry(group).ReloadAsync();
        var rotatedTargets = await _scenario.Admin.Context.ShareTargets
            .Where(t => t.ShareGroupId == group.Id && t.KeyVersion == newVersion)
            .ToListAsync();

        Assert.Equal(2, rotatedTargets.Count);
        var signer = new DeclarationSigner(_scenario.Crypto);
        foreach (var target in rotatedTargets)
        {
            Assert.NotEmpty(target.AdminSignature);
            Assert.Equal(_scenario.Admin.Keys.Ed25519PublicKey, target.GroupAdminEd25519PublicKey);

            var ok = await signer.VerifyShareTargetAsync(
                target.GroupAdminEd25519PublicKey,
                target.MemberPublicKey,
                target.Role,
                group.GroupContext,
                target.KeyVersion,
                target.AdminSignature);
            Assert.True(ok, $"Rotated AdminSignature on {target.MemberPublicKey} did not verify");
        }
    }

    // ----------------------------------------------------------------
    // HELPERS
    // ----------------------------------------------------------------

    [Theory]
    [InlineData("system:v1", 2, "system:v2")]
    [InlineData("shopping-list:v3", 4, "shopping-list:v4")]
    [InlineData("no-version", 1, "no-version:v1")]
    public void IncrementGroupContextVersion_WorksCorrectly(string input, int newVersion, string expected)
    {
        Assert.Equal(expected, GroupService.IncrementGroupContextVersion(input, newVersion));
    }
}
