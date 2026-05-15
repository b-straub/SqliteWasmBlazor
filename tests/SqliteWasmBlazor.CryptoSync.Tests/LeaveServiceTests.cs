using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

public class LeaveServiceTests : IAsyncLifetime
{
    private TwoActorBootstrap _scenario = null!;
    private LeaveService _userLeaveService = null!;
    private DeclarationSigner _signer = null!;

    public async Task InitializeAsync()
    {
        _scenario = await TwoActorBootstrap.CreateAsync();
        _signer = new DeclarationSigner(_scenario.Crypto);
        _userLeaveService = new LeaveService(_scenario.User.Context, _signer);
    }

    public async Task DisposeAsync()
    {
        await _scenario.DisposeAsync();
    }

    // ----------------------------------------------------------------
    // Happy path
    // ----------------------------------------------------------------

    [Fact]
    public async Task LeaveGroup_SoftDeletesShareTargetAndCreatesDeclaration()
    {
        var systemGroup = await _scenario.User.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);

        // User has a system ShareTarget before leaving.
        var targetBefore = await _scenario.User.Context.ShareTargets
            .SingleOrDefaultAsync(t =>
                t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == _scenario.User.Keys.X25519PublicKey);
        Assert.NotNull(targetBefore);

        var declaration = await _userLeaveService.LeaveGroupAsync(
            _scenario.User.Keys,
            CryptoSyncBootstrap.SystemGroupContext);

        // ShareTarget is soft-deleted.
        var targetAfter = await _scenario.User.Context.ShareTargets
            .IgnoreQueryFilters()
            .SingleAsync(t =>
                t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == _scenario.User.Keys.X25519PublicKey);
        Assert.True(targetAfter.IsDeleted);
        Assert.NotNull(targetAfter.DeletedAt);

        // LeaveDeclaration exists with correct fields.
        Assert.Equal(CryptoSyncBootstrap.SystemGroupContext, declaration.GroupContext);
        Assert.Equal(systemGroup.KeyVersion, declaration.KeyVersion);
        Assert.Equal(_scenario.User.Keys.Ed25519PublicKey, declaration.MemberEd25519PublicKey);
        Assert.NotEmpty(declaration.Signature);
    }

    // ----------------------------------------------------------------
    // Signature verification
    // ----------------------------------------------------------------

    [Fact]
    public async Task LeaveDeclaration_SignatureVerifies()
    {
        var declaration = await _userLeaveService.LeaveGroupAsync(
            _scenario.User.Keys,
            CryptoSyncBootstrap.SystemGroupContext);

        var ok = await _userLeaveService.VerifyLeaveDeclarationAsync(declaration);
        Assert.True(ok);
    }

    [Fact]
    public async Task LeaveDeclaration_TamperedGroupContext_SignatureFails()
    {
        var declaration = await _userLeaveService.LeaveGroupAsync(
            _scenario.User.Keys,
            CryptoSyncBootstrap.SystemGroupContext);

        // Tamper — change group context, verify with original signature.
        var tampered = new LeaveDeclaration
        {
            GroupContext = "tampered-group:v1",
            KeyVersion = declaration.KeyVersion,
            MemberEd25519PublicKey = declaration.MemberEd25519PublicKey,
            Signature = declaration.Signature
        };

        var ok = await _userLeaveService.VerifyLeaveDeclarationAsync(tampered);
        Assert.False(ok);
    }

    [Fact]
    public async Task LeaveDeclaration_WrongMemberKey_SignatureFails()
    {
        var declaration = await _userLeaveService.LeaveGroupAsync(
            _scenario.User.Keys,
            CryptoSyncBootstrap.SystemGroupContext);

        // Verify against the admin's key instead of the user's.
        var forged = new LeaveDeclaration
        {
            GroupContext = declaration.GroupContext,
            KeyVersion = declaration.KeyVersion,
            MemberEd25519PublicKey = _scenario.Admin.Keys.Ed25519PublicKey,
            Signature = declaration.Signature
        };

        var ok = await _userLeaveService.VerifyLeaveDeclarationAsync(forged);
        Assert.False(ok);
    }

    // ----------------------------------------------------------------
    // Edge cases
    // ----------------------------------------------------------------

    [Fact]
    public async Task LeaveGroup_NoShareTarget_Throws()
    {
        // User already left.
        await _userLeaveService.LeaveGroupAsync(
            _scenario.User.Keys,
            CryptoSyncBootstrap.SystemGroupContext);

        // Second leave fails — no active ShareTarget.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _userLeaveService.LeaveGroupAsync(
                _scenario.User.Keys,
                CryptoSyncBootstrap.SystemGroupContext).AsTask());
    }

    [Fact]
    public async Task LeaveGroup_NonExistentGroup_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _userLeaveService.LeaveGroupAsync(
                _scenario.User.Keys,
                "nonexistent-group:v1").AsTask());
    }
}
