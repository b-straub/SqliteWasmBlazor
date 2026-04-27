using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Read-side queries over the trusted-contact table. Contact creation +
/// trust establishment now lives in <see cref="ContactInvitationService"/>
/// (the contact's device builds a signed payload, the admin's device
/// verifies + accepts). What remains here is contact lookups + soft-delete.
/// </summary>
public class ContactService(CryptoSyncContextBase context)
{
    /// <summary>
    /// Revoke a previously verified/trusted contact. Does NOT rewrite
    /// <see cref="SyncableEntity.SharingId"/> or <see cref="SyncableEntity.SharingScope"/>
    /// (the immutable-SharingId rule forbids it). Sets
    /// <see cref="TrustedContact.Status"/> = <see cref="ContactStatus.Revoked"/>
    /// and bumps <see cref="SyncableEntity.UpdatedAt"/>. The interceptor handles
    /// the timestamp bump automatically.
    /// </summary>
    public async ValueTask UntrustAsync(Guid contactId)
    {
        var contact = await context.Contacts.FindAsync(contactId)
            ?? throw new InvalidOperationException($"Contact {contactId} not found");

        contact.Status = ContactStatus.Revoked;
        await context.SaveChangesAsync();
    }

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
            .Where(c => c.Status == ContactStatus.Verified || c.Status == ContactStatus.Trusted)
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
}
