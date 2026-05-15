using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.CryptoSync.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Admin-facing read/admin operations over the trusted-contact table.
/// Contact creation + trust establishment lives in
/// <see cref="ContactInvitationService"/>.
/// </summary>
internal class ContactService(
    CryptoSyncContextBase context,
    IGroupService groupService,
    IWhitelistPushService whitelistPush) : IContactService
{
    public async ValueTask<TrustedContact?> GetByEd25519PublicKeyAsync(string ed25519PublicKey)
    {
        return await context.Contacts.FirstOrDefaultAsync(c => c.Ed25519PublicKey == ed25519PublicKey);
    }

    public async ValueTask<List<TrustedContact>> GetAllAsync()
    {
        return await context.Contacts.ToListAsync();
    }

    public async ValueTask<string[]> GetRecipientPublicKeysAsync()
    {
        return await context.Contacts
            .Select(c => c.X25519PublicKey)
            .ToArrayAsync();
    }

    public async ValueTask DeleteAsync(Guid contactId)
    {
        var contact = await context.Contacts.FindAsync(contactId);
        if (contact is not null)
        {
            contact.IsDeleted = true;
            contact.DeletedAt = DateTime.UtcNow;
            contact.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// System admin revokes a contact end-to-end:
    /// <list type="number">
    ///   <item>Crypto-layer: rotate every group the contact is a regular
    ///         member of (via <see cref="GroupService.RemoveMemberAsync"/>).
    ///         The contact's own self-group is left alone — only the contact
    ///         can read it, and admin can't unwrap the CEK anyway.</item>
    ///   <item>Local soft-delete the <see cref="TrustedContact"/> row.</item>
    ///   <item>Network-layer: push <see cref="WhitelistOp.Revoke"/> for the
    ///         contact's Ed25519 hash so subsequent POSTs from that identity
    ///         are denied at the relay; GETs stay allowed for
    ///         <c>READ_GRACE_SECONDS</c> so the revoked device can pick up
    ///         the rotation envelopes that say "you've been revoked."</item>
    /// </list>
    /// </summary>
    public async ValueTask RevokeContactAsync(
        Guid contactId,
        DualKeyPairFull adminKeys,
        string deploymentSaltBase64,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adminKeys);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentSaltBase64);

        var contact = await context.Contacts
            .FirstOrDefaultAsync(c => c.Id == contactId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"ContactService.RevokeContactAsync: contact {contactId} not found.");
        if (contact.IsAdmin)
        {
            throw new InvalidOperationException(
                "ContactService.RevokeContactAsync: cannot revoke the system admin via this flow.");
        }

        var groupsToRotate = await context.ShareTargets
            .Where(t => t.MemberPublicKey == contact.X25519PublicKey)
            .Join(context.ShareGroups,
                t => t.ShareGroupId,
                g => g.Id,
                (t, g) => new { t.ShareGroupId, g.GroupAdminPublicKey })
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var entry in groupsToRotate)
        {
            // Skip the contact's own self-group — they're the admin there;
            // RemoveMemberAsync would refuse anyway (member-not-in-group),
            // and even if it succeeded we'd just re-key for nobody.
            if (entry.GroupAdminPublicKey == contact.X25519PublicKey)
            {
                continue;
            }
            await groupService
                .RemoveMemberAsync(entry.ShareGroupId, adminKeys, contact.X25519PublicKey)
                .ConfigureAwait(false);
        }

        var now = DateTime.UtcNow;
        contact.IsDeleted = true;
        contact.DeletedAt = now;
        contact.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var contactHash = WhitelistPushService.HashPubkey(deploymentSaltBase64, contact.Ed25519PublicKey);
        await WhitelistAdminFlow.PushAsync(
            whitelistPush, context, adminKeys,
            [WhitelistOp.Revoke(contactHash, DateTimeOffset.UtcNow.ToUnixTimeSeconds())],
            cancellationToken).ConfigureAwait(false);
    }
}
