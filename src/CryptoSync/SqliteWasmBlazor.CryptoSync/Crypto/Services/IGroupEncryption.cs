using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.Abstractions.Services;

/// <summary>
/// Group encryption operations — CEK management (control plane) and message encryption (data plane).
/// Implementation is provider-agnostic: composes ICryptoProvider primitives.
/// </summary>
public interface IGroupEncryption
{
    // ============================================================
    // CONTROL PLANE (Admin only)
    // ============================================================

    /// <summary>
    /// Creates a new encrypted group with a random CEK, wrapped for each member.
    /// </summary>
    /// <param name="adminPrivateKey">Admin's X25519 private key (32 bytes)</param>
    /// <param name="adminPublicKey">Admin's X25519 public key (Base64)</param>
    /// <param name="memberPublicKeys">All member X25519 public keys including admin (Base64)</param>
    /// <param name="groupContext">Versioned group identifier used as HKDF info (e.g., "group-abc:v1")</param>
    ValueTask<PrfResult<GroupKeyBundle>> CreateGroupKeysAsync(
        ReadOnlyMemory<byte> adminPrivateKey,
        string adminPublicKey,
        IReadOnlyList<string> memberPublicKeys,
        string groupContext);

    /// <summary>
    /// Adds new members to an existing group by wrapping the current CEK for them.
    /// </summary>
    /// <param name="adminPrivateKey">Admin's X25519 private key (32 bytes)</param>
    /// <param name="adminPublicKey">Admin's X25519 public key (Base64)</param>
    /// <param name="adminWrappedCek">Admin's own wrapped CEK (to unwrap the current CEK)</param>
    /// <param name="newMemberPublicKeys">New member X25519 public keys (Base64)</param>
    /// <param name="groupContext">Versioned group identifier used as HKDF info</param>
    ValueTask<PrfResult<IReadOnlyList<WrappedKey>>> AddGroupMembersAsync(
        ReadOnlyMemory<byte> adminPrivateKey,
        string adminPublicKey,
        SymmetricEncryptedData adminWrappedCek,
        IReadOnlyList<string> newMemberPublicKeys,
        string groupContext);

    /// <summary>
    /// Rotates the group key after member removal. Generates a new CEK and re-wraps for remaining members.
    /// Old CEK blobs remain valid for messages encrypted before rotation (forward secrecy, not backward).
    /// </summary>
    /// <param name="adminPrivateKey">Admin's X25519 private key (32 bytes)</param>
    /// <param name="adminPublicKey">Admin's X25519 public key (Base64)</param>
    /// <param name="remainingMemberPublicKeys">Public keys of members who should remain (including admin)</param>
    /// <param name="groupContext">New versioned group context with incremented version</param>
    ValueTask<PrfResult<GroupKeyBundle>> RotateGroupKeyAsync(
        ReadOnlyMemory<byte> adminPrivateKey,
        string adminPublicKey,
        IReadOnlyList<string> remainingMemberPublicKeys,
        string groupContext);

    /// <summary>
    /// Transfers admin role by re-wrapping all CEKs under the new admin's key.
    /// Both admins must be online — this is a two-party operation.
    /// </summary>
    /// <param name="oldAdminPrivateKey">Current admin's X25519 private key (32 bytes)</param>
    /// <param name="oldAdminPublicKey">Current admin's X25519 public key (Base64)</param>
    /// <param name="oldAdminWrappedCek">Current admin's wrapped CEK</param>
    /// <param name="newAdminPrivateKey">New admin's X25519 private key (32 bytes)</param>
    /// <param name="newAdminPublicKey">New admin's X25519 public key (Base64)</param>
    /// <param name="memberPublicKeys">All member public keys (including new admin)</param>
    /// <param name="groupContext">Group context string</param>
    /// <param name="keyVersion">Current key version</param>
    ValueTask<PrfResult<GroupKeyBundle>> TransferGroupAdminAsync(
        ReadOnlyMemory<byte> oldAdminPrivateKey,
        string oldAdminPublicKey,
        SymmetricEncryptedData oldAdminWrappedCek,
        ReadOnlyMemory<byte> newAdminPrivateKey,
        string newAdminPublicKey,
        IReadOnlyList<string> memberPublicKeys,
        string groupContext,
        int keyVersion);

    // ============================================================
    // DATA PLANE (Any member)
    // ============================================================

    /// <summary>
    /// Encrypts a message for the group. Unwraps the member's CEK, encrypts with AAD binding,
    /// and signs the envelope with Ed25519.
    /// </summary>
    /// <param name="senderX25519PrivateKey">Sender's X25519 private key for CEK unwrapping</param>
    /// <param name="senderEd25519PrivateKey">Sender's Ed25519 private key for envelope signing</param>
    /// <param name="senderEd25519PublicKey">Sender's Ed25519 public key (Base64) — included in message</param>
    /// <param name="adminPublicKey">Admin's X25519 public key (ECDH counterparty for unwrapping)</param>
    /// <param name="senderWrappedCek">Sender's wrapped CEK for this group</param>
    /// <param name="plaintext">The message to encrypt</param>
    /// <param name="groupContext">Group context — bound as AAD</param>
    /// <param name="keyVersion">Key version — bound as AAD</param>
    ValueTask<PrfResult<GroupEncryptedData>> EncryptForGroupAsync(
        ReadOnlyMemory<byte> senderX25519PrivateKey,
        ReadOnlyMemory<byte> senderEd25519PrivateKey,
        string senderEd25519PublicKey,
        string adminPublicKey,
        SymmetricEncryptedData senderWrappedCek,
        string plaintext,
        string groupContext,
        int keyVersion);

    /// <summary>
    /// Decrypts a group message. Verifies the envelope signature, unwraps CEK, and decrypts with AAD.
    /// </summary>
    /// <param name="recipientX25519PrivateKey">Recipient's X25519 private key for CEK unwrapping</param>
    /// <param name="adminPublicKey">Admin's X25519 public key (ECDH counterparty)</param>
    /// <param name="recipientWrappedCek">Recipient's wrapped CEK for this group + key version</param>
    /// <param name="data">The encrypted group message</param>
    ValueTask<PrfResult<string>> DecryptFromGroupAsync(
        ReadOnlyMemory<byte> recipientX25519PrivateKey,
        string adminPublicKey,
        SymmetricEncryptedData recipientWrappedCek,
        GroupEncryptedData data);
}
