using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using SqliteWasmBlazor.CryptoSync.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Manages sharing groups (ShareGroup + ShareTarget rows). Composes
/// <see cref="IGroupEncryption"/> crypto primitives with EF Core persistence.
///
/// <para>
/// All control-plane operations are admin-only. Every operation takes the
/// admin's <see cref="DualKeyPairFull"/> because the CEK wrapping key is
/// derived via ECDH(adminX25519Priv, adminX25519Pub) + HKDF(groupContext),
/// AND every new <see cref="ShareTarget"/> row is signed with the admin's
/// Ed25519 private key so receivers can verify the credential against the
/// admin's public key (the worker rejects unsigned ShareTargets at
/// <c>resolveSenderPermissions</c>).
/// </para>
/// </summary>
internal class GroupService(
    CryptoSyncContextBase context,
    IGroupEncryption groupEncryption,
    DeclarationSigner signer) : IGroupService
{
    /// <summary>
    /// Create a new sharing group with a random CEK wrapped for each member.
    /// Each new <see cref="ShareTarget"/> carries an Ed25519 credential
    /// signed by the admin so the receiver's permission resolver accepts it.
    /// </summary>
    public async ValueTask<ShareGroup> CreateGroupAsync(
        DualKeyPairFull adminKeys,
        IReadOnlyList<(string X25519PublicKey, SyncRole Role, Guid ContactId)> members,
        string groupContext)
    {
        var x25519Priv = adminKeys.X25519PrivateKey;
        var ed25519Priv = adminKeys.Ed25519PrivateKey;
        try
        {
            var memberPubKeys = members.Select(m => m.X25519PublicKey).ToList();
            var result = await groupEncryption.CreateGroupKeysAsync(
                x25519Priv, adminKeys.X25519PublicKey, memberPubKeys, groupContext);

            if (!result.Success)
            {
                throw new InvalidOperationException($"CreateGroupKeys failed: {result.ErrorCode}");
            }

            var bundle = result.Value!;
            var group = new ShareGroup
            {
                Id = Guid.NewGuid(),
                GroupContext = groupContext,
                KeyVersion = 1,
                GroupAdminPublicKey = adminKeys.X25519PublicKey,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SharingScope = SharingScope.PUBLIC,
                SharingId = CryptoSyncBootstrap.SystemSharingId
            };

            context.ShareGroups.Add(group);

            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                var wrappedKey = bundle.MemberKeys[i];
                var credSig = await signer.SignShareTargetAsync(
                    ed25519Priv, member.X25519PublicKey, member.Role,
                    groupContext, group.KeyVersion);

                context.ShareTargets.Add(new ShareTarget
                {
                    Id = Guid.NewGuid(),
                    ShareGroupId = group.Id,
                    KeyVersion = group.KeyVersion,
                    MemberPublicKey = member.X25519PublicKey,
                    WrappedContentKey = CryptoSyncBootstrap.SerializeWrappedCek(wrappedKey.WrappedContentKey),
                    Role = member.Role,
                    AdminSignature = credSig,
                    GroupAdminEd25519PublicKey = adminKeys.Ed25519PublicKey,
                    GrantedByContactId = member.ContactId,
                    UpdatedAt = DateTime.UtcNow,
                    SharingScope = SharingScope.PUBLIC,
                    SharingId = CryptoSyncBootstrap.SystemSharingId
                });
            }

            await context.SaveChangesAsync();
            return group;
        }
        finally
        {
            // Priv-key bytes alias adminKeys (a DualKeyPairFull); caller
            // owns the record lifecycle and invokes Clear() at EOL (P21).
        }
    }

    /// <summary>
    /// Add new members to an existing group by wrapping the current CEK for
    /// them. Each new <see cref="ShareTarget"/> carries an Ed25519 credential
    /// signed by the admin.
    /// </summary>
    public async ValueTask AddMembersAsync(
        Guid groupId,
        DualKeyPairFull adminKeys,
        IReadOnlyList<(string X25519PublicKey, SyncRole Role, Guid ContactId)> newMembers)
    {
        var group = await context.ShareGroups.FindAsync(groupId)
            ?? throw new InvalidOperationException($"ShareGroup {groupId} not found");

        var adminTarget = await context.ShareTargets
            .FirstOrDefaultAsync(t => t.ShareGroupId == groupId
                && t.MemberPublicKey == group.GroupAdminPublicKey
                && t.KeyVersion == group.KeyVersion)
            ?? throw new InvalidOperationException("Admin's own ShareTarget not found");

        var adminWrappedCek = CryptoSyncBootstrap.DeserializeWrappedCek(adminTarget.WrappedContentKey);
        var newPubKeys = newMembers.Select(m => m.X25519PublicKey).ToList();

        var x25519Priv = adminKeys.X25519PrivateKey;
        var ed25519Priv = adminKeys.Ed25519PrivateKey;
        try
        {
            var result = await groupEncryption.AddGroupMembersAsync(
                x25519Priv, group.GroupAdminPublicKey, adminWrappedCek, newPubKeys, group.GroupContext);

            if (!result.Success)
            {
                throw new InvalidOperationException($"AddGroupMembers failed: {result.ErrorCode}");
            }

            var wrappedKeys = result.Value!;
            for (var i = 0; i < newMembers.Count; i++)
            {
                var member = newMembers[i];
                var credSig = await signer.SignShareTargetAsync(
                    ed25519Priv, member.X25519PublicKey, member.Role,
                    group.GroupContext, group.KeyVersion);

                context.ShareTargets.Add(new ShareTarget
                {
                    Id = Guid.NewGuid(),
                    ShareGroupId = groupId,
                    KeyVersion = group.KeyVersion,
                    MemberPublicKey = member.X25519PublicKey,
                    WrappedContentKey = CryptoSyncBootstrap.SerializeWrappedCek(wrappedKeys[i].WrappedContentKey),
                    Role = member.Role,
                    AdminSignature = credSig,
                    GroupAdminEd25519PublicKey = adminKeys.Ed25519PublicKey,
                    GrantedByContactId = member.ContactId,
                    UpdatedAt = DateTime.UtcNow,
                    SharingScope = SharingScope.PUBLIC,
                    SharingId = CryptoSyncBootstrap.SystemSharingId
                });
            }

            group.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
        finally
        {
            // Priv-key bytes alias adminKeys (a DualKeyPairFull); caller
            // owns the record lifecycle and invokes Clear() at EOL (P21).
        }
    }

    /// <summary>
    /// Remove a member from a group. Rotates the CEK (increments key version)
    /// and re-wraps for remaining members. The removed member's old
    /// ShareTargets stay (for historical decryption) but no new-version target
    /// is issued. Each rotated <see cref="ShareTarget"/> carries an Ed25519
    /// credential signed by the admin.
    /// </summary>
    /// <returns>The new key version after rotation.</returns>
    public async ValueTask<int> RemoveMemberAsync(
        Guid groupId,
        DualKeyPairFull adminKeys,
        string memberToRemovePublicKey)
    {
        var group = await context.ShareGroups.FindAsync(groupId)
            ?? throw new InvalidOperationException($"ShareGroup {groupId} not found");

        var currentTargets = await context.ShareTargets
            .Where(t => t.ShareGroupId == groupId && t.KeyVersion == group.KeyVersion)
            .ToListAsync();

        var remainingTargets = currentTargets
            .Where(t => t.MemberPublicKey != memberToRemovePublicKey)
            .ToList();

        if (remainingTargets.Count == currentTargets.Count)
        {
            throw new InvalidOperationException(
                $"Member {memberToRemovePublicKey} not found in group {groupId}");
        }

        var remainingPubKeys = remainingTargets.Select(t => t.MemberPublicKey).ToList();

        // Increment version in group context: "system:v1" → "system:v2"
        var newKeyVersion = group.KeyVersion + 1;
        var newGroupContext = IncrementGroupContextVersion(group.GroupContext, newKeyVersion);

        var x25519Priv = adminKeys.X25519PrivateKey;
        var ed25519Priv = adminKeys.Ed25519PrivateKey;
        try
        {
            var result = await groupEncryption.RotateGroupKeyAsync(
                x25519Priv, group.GroupAdminPublicKey, remainingPubKeys, newGroupContext);

            if (!result.Success)
            {
                throw new InvalidOperationException($"RotateGroupKey failed: {result.ErrorCode}");
            }

            var bundle = result.Value!;
            group.KeyVersion = newKeyVersion;
            group.GroupContext = newGroupContext;
            group.UpdatedAt = DateTime.UtcNow;

            for (var i = 0; i < remainingTargets.Count; i++)
            {
                var oldTarget = remainingTargets[i];
                var credSig = await signer.SignShareTargetAsync(
                    ed25519Priv, oldTarget.MemberPublicKey, oldTarget.Role,
                    newGroupContext, newKeyVersion);

                context.ShareTargets.Add(new ShareTarget
                {
                    Id = Guid.NewGuid(),
                    ShareGroupId = groupId,
                    KeyVersion = newKeyVersion,
                    MemberPublicKey = oldTarget.MemberPublicKey,
                    WrappedContentKey = CryptoSyncBootstrap.SerializeWrappedCek(bundle.MemberKeys[i].WrappedContentKey),
                    Role = oldTarget.Role,
                    AdminSignature = credSig,
                    GroupAdminEd25519PublicKey = adminKeys.Ed25519PublicKey,
                    GrantedByContactId = oldTarget.GrantedByContactId,
                    UpdatedAt = DateTime.UtcNow,
                    SharingScope = SharingScope.PUBLIC,
                    SharingId = CryptoSyncBootstrap.SystemSharingId
                });
            }

            await context.SaveChangesAsync();
            return newKeyVersion;
        }
        finally
        {
            // Priv-key bytes alias adminKeys (a DualKeyPairFull); caller
            // owns the record lifecycle and invokes Clear() at EOL (P21).
        }
    }

    /// <summary>
    /// Update a member's role within a group.
    /// </summary>
    public async ValueTask UpdateMemberRoleAsync(
        Guid groupId,
        DualKeyPairFull adminKeys,
        string memberPublicKey,
        SyncRole newRole)
    {
        var group = await context.ShareGroups.FindAsync(groupId)
            ?? throw new InvalidOperationException($"ShareGroup {groupId} not found");

        if (group.GroupAdminPublicKey != adminKeys.X25519PublicKey)
        {
            throw new InvalidOperationException(
                "GroupService.UpdateMemberRoleAsync: caller is not the GroupAdmin of this group");
        }

        var target = await context.ShareTargets
            .FirstOrDefaultAsync(t => t.ShareGroupId == groupId
                && t.MemberPublicKey == memberPublicKey
                && t.KeyVersion == group.KeyVersion)
            ?? throw new InvalidOperationException(
                $"ShareTarget for member {memberPublicKey} not found in group {groupId}");

        target.Role = newRole;
        target.AdminSignature = await signer.SignShareTargetAsync(
            adminKeys.Ed25519PrivateKey, target.MemberPublicKey, newRole,
            group.GroupContext, target.KeyVersion);
        target.GroupAdminEd25519PublicKey = adminKeys.Ed25519PublicKey;
        target.UpdatedAt = DateTime.UtcNow;
        group.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Get all current members of a group (latest key version only).
    /// </summary>
    public async ValueTask<List<ShareTarget>> GetMembersAsync(Guid groupId)
    {
        var group = await context.ShareGroups.FindAsync(groupId)
            ?? throw new InvalidOperationException($"ShareGroup {groupId} not found");

        return await context.ShareTargets
            .Where(t => t.ShareGroupId == groupId && t.KeyVersion == group.KeyVersion)
            .ToListAsync();
    }

    /// <summary>
    /// Increment the version suffix in a group context string.
    /// E.g. "system:v1" → "system:v2", "shopping-list-abc:v3" → "shopping-list-abc:v4".
    /// </summary>
    internal static string IncrementGroupContextVersion(string groupContext, int newVersion)
    {
        var colonIdx = groupContext.LastIndexOf(":v", StringComparison.Ordinal);
        if (colonIdx < 0)
        {
            return $"{groupContext}:v{newVersion}";
        }
        return $"{groupContext[..colonIdx]}:v{newVersion}";
    }
}
