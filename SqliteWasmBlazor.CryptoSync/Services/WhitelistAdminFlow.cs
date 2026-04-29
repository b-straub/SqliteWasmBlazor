using SqliteWasmBlazor.Crypto.Abstractions.Models;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Shared admin-side glue for whitelist pushes that need version tracking +
/// single-retry-on-409. Used by <see cref="ContactInvitationService"/> at
/// invite/promote time and by <see cref="ContactService"/> at revoke time.
///
/// <para>
/// Reads the next version from <see cref="SyncState.LastWhitelistVersion"/>,
/// signs+POSTs via <see cref="IWhitelistPushService"/>, persists the
/// accepted version on success. On a single
/// <see cref="WhitelistVersionConflictException"/> (concurrent admin or
/// admin-recovery scenario), aligns local cursor to the relay's reported
/// <c>current_version</c> and retries once. A second 409 propagates to the
/// caller.
/// </para>
/// </summary>
internal static class WhitelistAdminFlow
{
    public static async ValueTask PushAsync(
        IWhitelistPushService whitelistPush,
        CryptoSyncContextBase context,
        DualKeyPairFull adminKeys,
        IReadOnlyList<WhitelistOp> ops,
        CancellationToken cancellationToken = default)
    {
        var adminEdPriv = Convert.FromBase64String(adminKeys.Ed25519PrivateKey);
        try
        {
            var state = await GetOrCreateSyncStateAsync(context, cancellationToken).ConfigureAwait(false);
            var attempts = 0;
            while (true)
            {
                var nextVersion = state.LastWhitelistVersion + 1;
                try
                {
                    var result = await whitelistPush.PushAsync(
                        ops,
                        adminKeys.Ed25519PublicKey,
                        adminEdPriv,
                        nextVersion,
                        cancellationToken).ConfigureAwait(false);
                    state.LastWhitelistVersion = result.Version;
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (WhitelistVersionConflictException ex) when (attempts == 0)
                {
                    state.LastWhitelistVersion = ex.CurrentVersion;
                    attempts++;
                }
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(adminEdPriv);
        }
    }

    private static async ValueTask<SyncState> GetOrCreateSyncStateAsync(
        CryptoSyncContextBase context, CancellationToken cancellationToken)
    {
        var row = await context.SyncStates
            .FirstOrDefaultAsync(s => s.Id == SyncState.EngineCursorId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new SyncState { Id = SyncState.EngineCursorId };
            context.SyncStates.Add(row);
        }
        return row;
    }
}
