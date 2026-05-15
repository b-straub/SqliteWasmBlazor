using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Verifies that all ShareTarget rows carry valid <see cref="ShareTarget.AdminSignature"/>
/// credentials signed by the correct GroupAdmin. These tests lock the signing
/// correctness before worker-side verification (Step 2b) is implemented.
/// </summary>
public class SignedShareTargetTests : IAsyncLifetime
{
    private TwoActorBootstrap _scenario = null!;
    private DeclarationSigner _signer = null!;

    public async Task InitializeAsync()
    {
        _scenario = await TwoActorBootstrap.CreateAsync();
        _signer = new DeclarationSigner(_scenario.Crypto);
    }

    public async Task DisposeAsync()
    {
        await _scenario.DisposeAsync();
    }

    // ----------------------------------------------------------------
    // Bootstrap-seeded targets
    // ----------------------------------------------------------------

    [Fact]
    public async Task AdminSystemTarget_SignatureVerifiesAgainstAdminEd25519()
    {
        var systemGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        var adminTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == _scenario.Admin.Keys.X25519PublicKey);

        Assert.NotEmpty(adminTarget.AdminSignature);
        Assert.Equal(_scenario.Admin.Keys.Ed25519PublicKey, adminTarget.GroupAdminEd25519PublicKey);

        var ok = await _signer.VerifyShareTargetAsync(
            adminTarget.GroupAdminEd25519PublicKey,
            adminTarget.MemberPublicKey,
            adminTarget.Role,
            systemGroup.GroupContext,
            adminTarget.KeyVersion,
            adminTarget.AdminSignature);
        Assert.True(ok);
    }

    [Fact]
    public async Task AdminSelfTarget_SignatureVerifiesAgainstAdminEd25519()
    {
        var selfGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext.StartsWith("self-")
                && g.GroupAdminPublicKey == _scenario.Admin.Keys.X25519PublicKey);
        var selfTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == selfGroup.Id);

        Assert.NotEmpty(selfTarget.AdminSignature);

        var ok = await _signer.VerifyShareTargetAsync(
            selfTarget.GroupAdminEd25519PublicKey,
            selfTarget.MemberPublicKey,
            selfTarget.Role,
            selfGroup.GroupContext,
            selfTarget.KeyVersion,
            selfTarget.AdminSignature);
        Assert.True(ok);
    }

    // ----------------------------------------------------------------
    // Invitation-created targets
    // ----------------------------------------------------------------

    [Fact]
    public async Task ContactSystemTarget_SignatureVerifiesAgainstAdminEd25519()
    {
        var systemGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        var userTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == _scenario.User.Keys.X25519PublicKey);

        Assert.NotEmpty(userTarget.AdminSignature);
        Assert.Equal(_scenario.Admin.Keys.Ed25519PublicKey, userTarget.GroupAdminEd25519PublicKey);

        var ok = await _signer.VerifyShareTargetAsync(
            userTarget.GroupAdminEd25519PublicKey,
            userTarget.MemberPublicKey,
            userTarget.Role,
            systemGroup.GroupContext,
            userTarget.KeyVersion,
            userTarget.AdminSignature);
        Assert.True(ok);
    }

    [Fact]
    public async Task ContactSelfTarget_SignatureVerifiesAgainstContactEd25519()
    {
        var userSelfGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext.StartsWith("self-")
                && g.GroupAdminPublicKey == _scenario.User.Keys.X25519PublicKey);
        var userSelfTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == userSelfGroup.Id);

        Assert.NotEmpty(userSelfTarget.AdminSignature);
        Assert.Equal(_scenario.User.Keys.Ed25519PublicKey, userSelfTarget.GroupAdminEd25519PublicKey);

        var ok = await _signer.VerifyShareTargetAsync(
            userSelfTarget.GroupAdminEd25519PublicKey,
            userSelfTarget.MemberPublicKey,
            userSelfTarget.Role,
            userSelfGroup.GroupContext,
            userSelfTarget.KeyVersion,
            userSelfTarget.AdminSignature);
        Assert.True(ok);
    }

    // ----------------------------------------------------------------
    // Tamper detection
    // ----------------------------------------------------------------

    [Fact]
    public async Task TamperedRole_SignatureFails()
    {
        var systemGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        var userTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == _scenario.User.Keys.X25519PublicKey);

        // Verify with the original role succeeds.
        var original = userTarget.Role;
        var ok = await _signer.VerifyShareTargetAsync(
            userTarget.GroupAdminEd25519PublicKey,
            userTarget.MemberPublicKey,
            original,
            systemGroup.GroupContext,
            userTarget.KeyVersion,
            userTarget.AdminSignature);
        Assert.True(ok);

        // Verify with an elevated role fails.
        var elevated = original == SyncRole.OWNER ? SyncRole.EDITOR : SyncRole.OWNER;
        var tampered = await _signer.VerifyShareTargetAsync(
            userTarget.GroupAdminEd25519PublicKey,
            userTarget.MemberPublicKey,
            elevated,
            systemGroup.GroupContext,
            userTarget.KeyVersion,
            userTarget.AdminSignature);
        Assert.False(tampered);
    }

    [Fact]
    public async Task WrongSigner_SignatureFails()
    {
        var systemGroup = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        var userTarget = await _scenario.Admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == _scenario.User.Keys.X25519PublicKey);

        // Verify against the user's key instead of the admin's — must fail.
        var ok = await _signer.VerifyShareTargetAsync(
            _scenario.User.Keys.Ed25519PublicKey,
            userTarget.MemberPublicKey,
            userTarget.Role,
            systemGroup.GroupContext,
            userTarget.KeyVersion,
            userTarget.AdminSignature);
        Assert.False(ok);
    }
}
