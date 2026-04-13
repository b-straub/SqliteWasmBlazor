using BlazorPRF.Crypto.Abstractions.Services;
using BlazorPRF.Crypto.Testing;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

public class GroupTransferServiceTests : IAsyncLifetime
{
    private TwoActorBootstrap _scenario = null!;
    private GroupTransferService _transferService = null!;
    private DeclarationSigner _signer = null!;

    public async Task InitializeAsync()
    {
        _scenario = await TwoActorBootstrap.CreateAsync();
        _signer = new DeclarationSigner(_scenario.Crypto);
        _transferService = new GroupTransferService(
            _scenario.Admin.Context, _scenario.GroupEncryption, _signer);
    }

    public async Task DisposeAsync()
    {
        await _scenario.DisposeAsync();
    }

    // ----------------------------------------------------------------
    // Phase 1 — Release
    // ----------------------------------------------------------------

    [Fact]
    public async Task ReleaseGroup_CreatesDeclarationAndWrapsForNewAdmin()
    {
        var declaration = await _transferService.ReleaseGroupAsync(
            _scenario.Admin.Keys,
            _scenario.User.Keys.X25519PublicKey,
            _scenario.User.Keys.Ed25519PublicKey,
            CryptoSyncBootstrap.SystemGroupContext);

        Assert.Equal(CryptoSyncBootstrap.SystemGroupContext, declaration.GroupContext);
        Assert.Equal(_scenario.Admin.Keys.Ed25519PublicKey, declaration.OldGroupAdminEd25519PublicKey);
        Assert.Equal(_scenario.User.Keys.Ed25519PublicKey, declaration.NewGroupAdminEd25519PublicKey);
        Assert.NotEmpty(declaration.Signature);
        Assert.False(declaration.IsClaimed);

        // New admin has a ShareTarget at the current KeyVersion.
        var group = await _scenario.Admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        var newAdminTarget = await _scenario.Admin.Context.ShareTargets
            .SingleOrDefaultAsync(t =>
                t.ShareGroupId == group.Id
                && t.MemberPublicKey == _scenario.User.Keys.X25519PublicKey
                && t.KeyVersion == group.KeyVersion);
        Assert.NotNull(newAdminTarget);
        Assert.Equal(SyncRole.OWNER, newAdminTarget.Role);
    }

    [Fact]
    public async Task ReleaseGroup_DeclarationSignatureVerifies()
    {
        var declaration = await _transferService.ReleaseGroupAsync(
            _scenario.Admin.Keys,
            _scenario.User.Keys.X25519PublicKey,
            _scenario.User.Keys.Ed25519PublicKey,
            CryptoSyncBootstrap.SystemGroupContext);

        var ok = await _signer.VerifyTransferDeclarationAsync(
            declaration.OldGroupAdminEd25519PublicKey,
            declaration.GroupContext,
            declaration.NewGroupAdminEd25519PublicKey,
            declaration.Signature);
        Assert.True(ok);
    }

    [Fact]
    public async Task ReleaseGroup_NonAdmin_Throws()
    {
        // User is not the group admin — should fail.
        var userTransferService = new GroupTransferService(
            _scenario.Admin.Context, _scenario.GroupEncryption, _signer);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            userTransferService.ReleaseGroupAsync(
                _scenario.User.Keys,
                _scenario.Admin.Keys.X25519PublicKey,
                _scenario.Admin.Keys.Ed25519PublicKey,
                CryptoSyncBootstrap.SystemGroupContext).AsTask());
    }

    [Fact]
    public async Task ReleaseGroup_DoublePending_Throws()
    {
        await _transferService.ReleaseGroupAsync(
            _scenario.Admin.Keys,
            _scenario.User.Keys.X25519PublicKey,
            _scenario.User.Keys.Ed25519PublicKey,
            CryptoSyncBootstrap.SystemGroupContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _transferService.ReleaseGroupAsync(
                _scenario.Admin.Keys,
                _scenario.User.Keys.X25519PublicKey,
                _scenario.User.Keys.Ed25519PublicKey,
                CryptoSyncBootstrap.SystemGroupContext).AsTask());
    }

    // ----------------------------------------------------------------
    // Phase 2 — Claim
    // ----------------------------------------------------------------

    [Fact]
    public async Task ClaimGroup_RotatesKeysAndUpdatesAdmin()
    {
        await _transferService.ReleaseGroupAsync(
            _scenario.Admin.Keys,
            _scenario.User.Keys.X25519PublicKey,
            _scenario.User.Keys.Ed25519PublicKey,
            CryptoSyncBootstrap.SystemGroupContext);

        // Mirror the transfer declaration + new admin's ShareTarget to user's DB.
        await MirrorReleaseToUserDbAsync();

        var userTransferService = new GroupTransferService(
            _scenario.User.Context, _scenario.GroupEncryption, _signer);

        var updatedGroup = await userTransferService.ClaimGroupAsync(
            _scenario.User.Keys,
            CryptoSyncBootstrap.SystemGroupContext);

        // Group admin is now the user.
        Assert.Equal(_scenario.User.Keys.X25519PublicKey, updatedGroup.GroupAdminPublicKey);
        Assert.Equal(2, updatedGroup.KeyVersion);

        // Transfer declaration is marked as claimed.
        var declaration = await _scenario.User.Context.TransferDeclarations
            .SingleAsync(td => td.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        Assert.True(declaration.IsClaimed);

        // New ShareTargets exist at KeyVersion 2.
        var newTargets = await _scenario.User.Context.ShareTargets
            .Where(t => t.ShareGroupId == updatedGroup.Id && t.KeyVersion == 2)
            .ToListAsync();
        Assert.True(newTargets.Count > 0);
        Assert.All(newTargets, t =>
            Assert.Equal(_scenario.User.Keys.Ed25519PublicKey, t.GroupAdminEd25519PublicKey));
    }

    [Fact]
    public async Task ClaimGroup_NoPendingTransfer_Throws()
    {
        var userTransferService = new GroupTransferService(
            _scenario.User.Context, _scenario.GroupEncryption, _signer);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            userTransferService.ClaimGroupAsync(
                _scenario.User.Keys,
                CryptoSyncBootstrap.SystemGroupContext).AsTask());
    }

    // ----------------------------------------------------------------
    // Full round-trip: new admin can unwrap CEK
    // ----------------------------------------------------------------

    [Fact]
    public async Task FullTransfer_NewAdminCanUnwrapRotatedCek()
    {
        await _transferService.ReleaseGroupAsync(
            _scenario.Admin.Keys,
            _scenario.User.Keys.X25519PublicKey,
            _scenario.User.Keys.Ed25519PublicKey,
            CryptoSyncBootstrap.SystemGroupContext);

        await MirrorReleaseToUserDbAsync();

        var userTransferService = new GroupTransferService(
            _scenario.User.Context, _scenario.GroupEncryption, _signer);

        var updatedGroup = await userTransferService.ClaimGroupAsync(
            _scenario.User.Keys,
            CryptoSyncBootstrap.SystemGroupContext);

        // New admin unwraps their CEK at the new KeyVersion.
        var newTarget = await _scenario.User.Context.ShareTargets
            .SingleAsync(t =>
                t.ShareGroupId == updatedGroup.Id
                && t.MemberPublicKey == _scenario.User.Keys.X25519PublicKey
                && t.KeyVersion == updatedGroup.KeyVersion);

        var wrapped = CryptoSyncBootstrap.DeserializeWrappedCek(newTarget.WrappedContentKey);
        var userPriv = Convert.FromBase64String(_scenario.User.Keys.X25519PrivateKey);
        var wk = await _scenario.Crypto.DeriveWrappingKeyAsync(
            userPriv, _scenario.User.Keys.X25519PublicKey, updatedGroup.GroupContext);
        Assert.True(wk.Success);

        var cek = await _scenario.Crypto.UnwrapContentKeyAsync(wrapped, wk.Value!);
        Assert.True(cek.Success);
        Assert.Equal(32, cek.Value!.Length);
    }

    // ----------------------------------------------------------------
    // Helper — mirror release artifacts to user's DB (simulates sync)
    // ----------------------------------------------------------------

    private async Task MirrorReleaseToUserDbAsync()
    {
        var adminCtx = _scenario.Admin.Context;
        var userCtx = _scenario.User.Context;

        // Mirror the TransferDeclaration.
        var declaration = await adminCtx.TransferDeclarations
            .SingleAsync(td => td.GroupContext == CryptoSyncBootstrap.SystemGroupContext);

        userCtx.TransferDeclarations.Add(new TransferDeclaration
        {
            Id = declaration.Id,
            GroupContext = declaration.GroupContext,
            OldGroupAdminEd25519PublicKey = declaration.OldGroupAdminEd25519PublicKey,
            NewGroupAdminEd25519PublicKey = declaration.NewGroupAdminEd25519PublicKey,
            Signature = declaration.Signature,
            IsClaimed = declaration.IsClaimed,
            SharingScope = declaration.SharingScope,
            SharingId = declaration.SharingId,
            UpdatedAt = declaration.UpdatedAt
        });

        // Mirror the new admin's ShareTarget (created by Phase 1).
        var group = await adminCtx.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        var newAdminTarget = await adminCtx.ShareTargets
            .OrderByDescending(t => t.UpdatedAt)
            .FirstAsync(t =>
                t.ShareGroupId == group.Id
                && t.MemberPublicKey == _scenario.User.Keys.X25519PublicKey
                && t.KeyVersion == group.KeyVersion);

        // Check if it already exists in user DB (from TwoActorBootstrap).
        var existsInUser = await userCtx.ShareTargets
            .IgnoreQueryFilters()
            .AnyAsync(t => t.Id == newAdminTarget.Id);

        if (!existsInUser)
        {
            userCtx.ShareTargets.Add(new ShareTarget
            {
                Id = newAdminTarget.Id,
                ShareGroupId = newAdminTarget.ShareGroupId,
                KeyVersion = newAdminTarget.KeyVersion,
                MemberPublicKey = newAdminTarget.MemberPublicKey,
                WrappedContentKey = newAdminTarget.WrappedContentKey,
                Role = newAdminTarget.Role,
                AdminSignature = newAdminTarget.AdminSignature,
                GroupAdminEd25519PublicKey = newAdminTarget.GroupAdminEd25519PublicKey,
                GrantedByContactId = newAdminTarget.GrantedByContactId,
                SharingScope = newAdminTarget.SharingScope,
                SharingId = newAdminTarget.SharingId,
                UpdatedAt = newAdminTarget.UpdatedAt
            });
        }

        await userCtx.SaveChangesAsync();
    }
}
