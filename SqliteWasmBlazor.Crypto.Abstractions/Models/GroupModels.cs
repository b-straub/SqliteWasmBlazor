namespace SqliteWasmBlazor.Crypto.Abstractions.Models;

/// <summary>
/// A content encryption key (CEK) wrapped for a specific group member.
/// </summary>
/// <param name="MemberPublicKey">The member's X25519 public key (Base64)</param>
/// <param name="WrappedContentKey">The CEK encrypted with the ECDH-derived wrapping key</param>
public sealed record WrappedKey(
    string MemberPublicKey,
    SymmetricEncryptedData WrappedContentKey
);

/// <summary>
/// Complete key bundle for a group — contains wrapped CEKs for all members.
/// </summary>
/// <param name="GroupContext">Versioned group identifier (e.g., "group-abc:v1"), used as HKDF info</param>
/// <param name="KeyVersion">Key version counter, increments on each rotation</param>
/// <param name="AdminPublicKey">The admin's X25519 public key (Base64) — ECDH counterparty for unwrapping</param>
/// <param name="MemberKeys">Wrapped CEK for each member (including admin)</param>
public sealed record GroupKeyBundle(
    string GroupContext,
    int KeyVersion,
    string AdminPublicKey,
    IReadOnlyList<WrappedKey> MemberKeys
);

/// <summary>
/// An encrypted message within a group, with tamper detection metadata.
/// </summary>
/// <param name="GroupContext">Group identifier — bound as AAD, selecting which CEK to use</param>
/// <param name="KeyVersion">Key version — bound as AAD, selecting which wrapped CEK version</param>
/// <param name="Encrypted">AES-256-GCM encrypted content (ciphertext + nonce)</param>
/// <param name="SenderPublicKey">Sender's Ed25519 public key for signature verification</param>
/// <param name="EnvelopeSignature">Ed25519 signature over canonical envelope (tamper detection)</param>
public sealed record GroupEncryptedData(
    string GroupContext,
    int KeyVersion,
    SymmetricEncryptedData Encrypted,
    string SenderPublicKey,
    string EnvelopeSignature
);
