using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.CryptoSync.Abstractions;

/// <summary>
/// Voluntary leave-group flow (protocol op O7). The departing member signs a
/// <see cref="LeaveDeclaration"/>, soft-deletes their own <see cref="ShareTarget"/>,
/// and persists both in one transaction. The declaration is cryptographic proof
/// of voluntary leave — verifiable against the member's
/// <see cref="TrustedContact.Ed25519PublicKey"/>. Cooperative only; the
/// GroupAdmin still rotates keys (O6) to cryptographically lock out the
/// departed member.
/// </summary>
public interface ILeaveService
{
    /// <summary>
    /// Leave a group: sign a <see cref="LeaveDeclaration"/> and soft-delete
    /// the member's own <see cref="ShareTarget"/>.
    /// </summary>
    ValueTask<LeaveDeclaration> LeaveGroupAsync(
        DualKeyPairFull memberKeys,
        string groupContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verify a <see cref="LeaveDeclaration"/> is authentic — the signature
    /// matches the claimed member's Ed25519 public key.
    /// </summary>
    ValueTask<bool> VerifyLeaveDeclarationAsync(LeaveDeclaration declaration);
}
