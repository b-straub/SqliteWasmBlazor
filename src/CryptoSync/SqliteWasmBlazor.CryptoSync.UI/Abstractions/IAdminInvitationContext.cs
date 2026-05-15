using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.CryptoSync.UI.Services;

/// <summary>
/// Host-supplied seam carrying the secrets <see cref="ContactInvitationService"/>
/// needs from a panel context: the deployment admin's WebAuthn-derived
/// key material, the deployment salt, and the active relay transport. The
/// CryptoSync.UI library does not register a default — the post-Stage-2
/// demo step provides the PRF-backed implementation.
///
/// <para>
/// All getters are awaitable so an implementation can lazily run a
/// WebAuthn assertion / PRF derivation on first access (within the same
/// Blazor render context as the panel).
/// </para>
/// </summary>
public interface IAdminInvitationContext
{
    ValueTask<DualKeyPairFull> GetAdminKeysAsync(CancellationToken cancellationToken = default);
    ValueTask<string> GetDeploymentSaltBase64Async(CancellationToken cancellationToken = default);
    ValueTask<ISyncTransport> GetSyncTransportAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Host-supplied seam carrying the contact's WebAuthn-derived keys + the
/// active relay transport for the invitation-acceptance flow. Mirrors
/// <see cref="IAdminInvitationContext"/> but without the deployment salt
/// (member devices don't sign whitelist ops).
/// </summary>
public interface IContactInvitationContext
{
    ValueTask<DualKeyPairFull> GetContactKeysAsync(CancellationToken cancellationToken = default);
    ValueTask<ISyncTransport> GetSyncTransportAsync(CancellationToken cancellationToken = default);
}
