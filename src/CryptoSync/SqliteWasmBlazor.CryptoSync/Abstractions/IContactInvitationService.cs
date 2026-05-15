using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.CryptoSync.Abstractions;

/// <summary>
/// Admin-initiated invitation flow plus the invitee-side response broadcast.
/// Implements the three phases of the OOB-bootstrap handshake:
/// admin <c>CreateInvitation</c> → invitee <c>RespondToInvitation</c> →
/// admin <c>IngestInvitationResponses</c> + <c>PromoteInvitation</c>.
/// </summary>
public interface IContactInvitationService
{
    /// <summary>
    /// Admin-side: build an invitation channel (transport keypair) and an
    /// <see cref="InvitationBundle"/> the admin ships out-of-band.
    /// </summary>
    ValueTask<InvitationBundle> CreateInvitationAsync(
        DualKeyPairFull adminKeys,
        string deploymentSaltBase64,
        string username,
        string? email = null,
        string? comment = null,
        string? relayHint = null,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-delete invitations whose <see cref="Invitation.ExpiresAt"/> is in
    /// the past. Cleans up the invitation share group + ShareTargets, and
    /// pushes a batched <c>WhitelistOp.Revoke</c> for every expired
    /// transport Ed25519 key so the relay stops accepting POSTs from them.
    /// </summary>
    ValueTask DeleteExpiredInvitationsAsync(
        DualKeyPairFull adminKeys,
        string deploymentSaltBase64,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-delete a single invitation by id (admin revoke). Pushes
    /// <c>WhitelistOp.Revoke</c> for the invitation's transport Ed25519 key
    /// before removing the local rows, so an in-flight invitee cannot POST
    /// further envelopes through the channel.
    /// </summary>
    ValueTask RevokeInvitationAsync(
        Guid invitationId,
        DualKeyPairFull adminKeys,
        string deploymentSaltBase64,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin-side: drain pending response envelopes from the transport,
    /// decrypt, verify, and update the matching <see cref="Invitation"/> rows.
    /// Returns the count of rows updated.
    /// </summary>
    ValueTask<int> IngestInvitationResponsesAsync(
        DualKeyPairFull adminKeys,
        ISyncTransport syncTransport,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin-side: promote a responded <see cref="Invitation"/> to a real
    /// <see cref="TrustedContact"/>. The local DB changes (insert contact +
    /// self ShareGroup/ShareTarget + system ShareTarget + delete invitation
    /// channel) commit in one EF transaction; the matching relay whitelist
    /// transition (<c>Revoke transport + Add contact</c>) is pushed BEFORE
    /// that commit. A relay-push failure leaves both sides unchanged
    /// (invitation still pending, admin can retry). A relay-success /
    /// local-commit failure leaves the relay benignly pre-whitelisting a key
    /// with no matching local Contact row — retry-safe because relay
    /// whitelist ops are idempotent.
    /// </summary>
    ValueTask<TrustedContact> PromoteInvitationAsync(
        Guid invitationId,
        DualKeyPairFull adminKeys,
        string deploymentSaltBase64,
        SyncRole systemRole = SyncRole.EDITOR,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invitee-side: derive the transport keypair from the bundle's shared
    /// secret, sign the canonical contact payload, encrypt the response
    /// envelope under the invitation group's HKDF wrapping key, and broadcast
    /// it via the supplied transport.
    /// </summary>
    ValueTask RespondToInvitationAsync(
        InvitationBundle bundle,
        DualKeyPairFull contactKeys,
        ContactUserData userData,
        ISyncTransport syncTransport,
        Guid? proposedContactId = null,
        CancellationToken cancellationToken = default);
}
