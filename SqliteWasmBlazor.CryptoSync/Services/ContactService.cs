using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Manages trusted contacts. Plain user data (no per-column encryption — see Phase H
/// for the at-rest defense model). System table; only the admin device creates
/// contacts (decision §12), other devices receive them via the public-scope sync
/// once promoted to <see cref="TrustLevel.Full"/>.
///
/// Phase B minimal surface — Phase E expands to the full canonical API
/// (List/UpdateUserData/UpdateTrustLevel/Delete/etc.).
/// </summary>
public class ContactService(CryptoSyncContextBase context)
{
    /// <summary>
    /// Create a new trusted contact at <see cref="TrustLevel.Marginal"/>.
    /// The row stays admin-private (<see cref="SharingScope.Client"/>) until
    /// <c>ContactPromotionService.ElevateToFullAsync</c> flips it to
    /// <see cref="SharingScope.Public"/>.
    /// </summary>
    public async ValueTask<TrustedContact> AddContactAsync(
        ContactUserData userData,
        string x25519PublicKey,
        string ed25519PublicKey,
        SyncRole role,
        TrustLevel trustLevel,
        TrustDirection direction)
    {
        var now = DateTime.UtcNow;
        var contact = new TrustedContact
        {
            Id = Guid.NewGuid(),
            Username = userData.Username,
            Email = userData.Email,
            Comment = userData.Comment,
            X25519PublicKey = x25519PublicKey,
            Ed25519PublicKey = ed25519PublicKey,
            Role = role,
            TrustLevel = trustLevel,
            Direction = direction,
            VerifiedAt = now,
            UpdatedAt = now,
            SharingScope = SharingScope.Client,
            SharingId = string.Empty
        };

        context.Contacts.Add(contact);
        await context.SaveChangesAsync();
        return contact;
    }

    /// <summary>
    /// Get a contact by Ed25519 public key (used to identify delta senders).
    /// </summary>
    public async ValueTask<TrustedContact?> GetByEd25519PublicKeyAsync(string ed25519PublicKey)
    {
        return await context.Contacts.FirstOrDefaultAsync(c => c.Ed25519PublicKey == ed25519PublicKey);
    }

    /// <summary>
    /// Get all contacts.
    /// </summary>
    public async ValueTask<List<TrustedContact>> GetAllAsync()
    {
        return await context.Contacts.ToListAsync();
    }

    /// <summary>
    /// Get X25519 public keys of all contacts (for building recipient list).
    /// </summary>
    public async ValueTask<string[]> GetRecipientPublicKeysAsync()
    {
        return await context.Contacts
            .Select(c => c.X25519PublicKey)
            .ToArrayAsync();
    }

    /// <summary>
    /// Update a contact's role.
    /// </summary>
    public async ValueTask UpdateRoleAsync(Guid contactId, SyncRole newRole)
    {
        var contact = await context.Contacts.FindAsync(contactId)
            ?? throw new InvalidOperationException($"Contact {contactId} not found");

        contact.Role = newRole;
        contact.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Remove a contact (soft delete via <see cref="SyncableEntity.IsDeleted"/>).
    /// </summary>
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
