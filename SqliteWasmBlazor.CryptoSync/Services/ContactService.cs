using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Admin-facing operations over the trusted-contact table:
/// admin-initiated invitation creation (Stage 4b), contact lookups, revoke,
/// soft-delete. The mirror operation — accepting a contact's response
/// payload — lives in <see cref="ContactInvitationService"/>.
/// </summary>
public class ContactService(CryptoSyncContextBase context)
{
    /// <summary>Length of the one-shot invitation token in bytes.</summary>
    public const int InvitationTokenSize = 32;

    /// <summary>
    /// Admin-side: create a placeholder <see cref="TrustedContact"/> in
    /// <see cref="ContactStatus.Invited"/> with a fresh 32-byte one-shot
    /// <see cref="TrustedContact.InvitationToken"/>, no contact pubkeys yet.
    /// Returns an <see cref="InvitationBundle"/> the admin ships out-of-band
    /// (QR / email / messenger). The contact uses the bundle to reply
    /// through <c>ISyncTransport</c>; admin matches the token at acceptance
    /// and binds pubkeys, transitioning the row to
    /// <see cref="ContactStatus.Verified"/>.
    /// </summary>
    /// <param name="username">Display name for the new contact.</param>
    /// <param name="email">Optional email — admin may not know it yet;
    /// the contact's response payload provides the authoritative value.</param>
    /// <param name="comment">Optional admin-side comment.</param>
    /// <param name="relayHint">Optional relay URL embedded in the bundle.</param>
    public async ValueTask<InvitationBundle> CreateInvitationAsync(
        string username,
        string? email = null,
        string? comment = null,
        string? relayHint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var admin = await context.Contacts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsAdmin, cancellationToken)
            ?? throw new InvalidOperationException(
                "ContactService.CreateInvitationAsync: no admin TrustedContact found in local DB. " +
                "Invitations can only be created on a fully bootstrapped admin device.");
        var adminX25519PublicKey = admin.X25519PublicKey
            ?? throw new InvalidOperationException(
                "ContactService.CreateInvitationAsync: admin TrustedContact has null X25519PublicKey — bootstrap invariant violated.");

        var token = RandomNumberGenerator.GetBytes(InvitationTokenSize);
        var now = DateTime.UtcNow;

        context.Contacts.Add(new TrustedContact
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            Comment = comment,
            X25519PublicKey = null,
            Ed25519PublicKey = null,
            IsAdmin = false,
            Status = ContactStatus.Invited,
            InvitationToken = token,
            InvitedAt = now,
            UpdatedAt = now,
            SharingScope = SharingScope.PUBLIC,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new InvitationBundle
        {
            Token = token,
            AdminX25519PublicKey = adminX25519PublicKey,
            RelayHint = relayHint
        };
    }

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
        // Status guarantees pubkey was bound at the Verified transition, but
        // the C# nullable type system can't see that — materialize, then
        // assert. A null here is an invariant violation, not a routine miss.
        var rows = await context.Contacts
            .Where(c => c.Status == ContactStatus.Verified || c.Status == ContactStatus.Trusted)
            .Select(c => new { c.Id, c.X25519PublicKey })
            .ToArrayAsync();

        var keys = new string[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            keys[i] = rows[i].X25519PublicKey
                ?? throw new InvalidOperationException(
                    $"Contact {rows[i].Id:N} is {nameof(ContactStatus.Verified)}/{nameof(ContactStatus.Trusted)} but X25519PublicKey is null.");
        }
        return keys;
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
