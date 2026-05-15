using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.CryptoSync.Abstractions;

/// <summary>
/// Manages sharing groups (<see cref="ShareGroup"/> + <see cref="ShareTarget"/>
/// rows). Composes group-encryption crypto primitives with EF Core persistence.
/// All control-plane operations are admin-only.
/// </summary>
public interface IGroupService
{
    /// <summary>
    /// Create a new sharing group with a random CEK wrapped for each member.
    /// </summary>
    ValueTask<ShareGroup> CreateGroupAsync(
        DualKeyPairFull adminKeys,
        IReadOnlyList<(string X25519PublicKey, SyncRole Role, Guid ContactId)> members,
        string groupContext);

    /// <summary>
    /// Add new members to an existing group by wrapping the current CEK for them.
    /// </summary>
    ValueTask AddMembersAsync(
        Guid groupId,
        DualKeyPairFull adminKeys,
        IReadOnlyList<(string X25519PublicKey, SyncRole Role, Guid ContactId)> newMembers);

    /// <summary>
    /// Remove a member from a group — rotates the CEK and re-wraps for the
    /// remaining members. Returns the new key version.
    /// </summary>
    ValueTask<int> RemoveMemberAsync(
        Guid groupId,
        DualKeyPairFull adminKeys,
        string memberToRemovePublicKey);

    /// <summary>Update a member's role within a group.</summary>
    ValueTask UpdateMemberRoleAsync(
        Guid groupId,
        DualKeyPairFull adminKeys,
        string memberPublicKey,
        SyncRole newRole);

    /// <summary>Get all current members of a group (latest key version only).</summary>
    ValueTask<List<ShareTarget>> GetMembersAsync(Guid groupId);
}
