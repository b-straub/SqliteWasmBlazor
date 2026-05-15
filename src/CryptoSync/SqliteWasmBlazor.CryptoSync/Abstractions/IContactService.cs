using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.CryptoSync.Abstractions;

/// <summary>
/// Admin-facing read/admin operations over the trusted-contact table.
/// Contact creation + trust establishment lives in
/// <see cref="IContactInvitationService"/>.
/// </summary>
public interface IContactService
{
    ValueTask<TrustedContact?> GetByEd25519PublicKeyAsync(string ed25519PublicKey);
    ValueTask<List<TrustedContact>> GetAllAsync();
    ValueTask<string[]> GetRecipientPublicKeysAsync();
    ValueTask DeleteAsync(Guid contactId);

    /// <summary>
    /// System admin revokes a contact end-to-end: rotates every shared group
    /// the contact is a regular member of, soft-deletes the
    /// <see cref="TrustedContact"/> row, and pushes a relay-whitelist
    /// <c>Revoke</c> op for the contact's Ed25519 hash.
    /// </summary>
    ValueTask RevokeContactAsync(
        Guid contactId,
        DualKeyPairFull adminKeys,
        string deploymentSaltBase64,
        CancellationToken cancellationToken = default);
}
