using System.Security.Cryptography;
using RxBlazorV2.Model;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.Crypto.UI.Components.Authentication;

namespace SqliteWasmBlazor.Crypto.UI.Services;

/// <summary>
/// Crypto.UI singleton ObservableModel that keeps the worker-side
/// encrypted-VFS global key in lockstep with the C# auth state.
/// Replaces the per-DB <c>EncryptedDatabaseLifecycle</c> from the
/// pre-fork branch — the single-key VFS model on <c>key-mgmt-v2</c>
/// needs only a single Unlock/Lock pair, not a registration set.
///
/// <para>
/// <b>Observation pattern.</b> The lifecycle is an
/// <see cref="ObservableModel"/> rather than a plain service so it can
/// react to <see cref="AuthenticationModel.PublicKey"/> changes via
/// the auto-detected internal observer pattern (RxBlazorV2 §7). The
/// SG sees <see cref="OnAuthPublicKeyChangedAsync"/> accesses
/// <c>Auth.PublicKey</c> and wires the subscription automatically — no
/// <c>event +=</c>, no <c>Subscriptions.Add</c>, no operation gate, no
/// <c>_lastIdentity</c> tracker.
/// </para>
///
/// <para>
/// <b>Two transitions.</b>
/// <list type="bullet">
///   <item><b>Identity set + VFS Encrypted+Locked:</b> derive a 32-byte
///         domain-separated VFS key from the cached PRF seed via
///         <see cref="IPrfService.DeriveDomainKeyAsync"/>, hand it to
///         <see cref="IEncryptedSqliteWasmDatabaseService.UnlockAsync"/>, and zero the
///         C#-side copy immediately.</item>
///   <item><b>Identity cleared / TTL elapsed + VFS Unlocked:</b> call
///         <see cref="IEncryptedSqliteWasmDatabaseService.LockAsync"/>. TTL means
///         "lock the keys" — the worker registry surviving past the
///         cache TTL would let the worker decrypt pages without an
///         active session.</item>
/// </list>
/// </para>
///
/// <para>
/// Lifetime is Singleton via <c>[ObservableModelScope(ModelScope.Singleton)]</c>.
/// Hosts eagerly resolve via <c>UseEncryptedDiskLifecycle()</c> after
/// <c>builder.Build()</c> so the first auth event arrives with the
/// observer already wired.
/// </para>
/// </summary>
[ObservableModelScope(ModelScope.Singleton)]
internal partial class EncryptedDiskLifecycle : ObservableModel
{
    /// <summary>
    /// Reserved <c>domainId</c> passed to
    /// <see cref="IPrfService.DeriveDomainKeyAsync"/>. Stored in the
    /// secure cache as <c>prf-domain:vfs</c>.
    /// </summary>
    public const string VfsDomainId = "vfs";

    /// <summary>
    /// HKDF <c>info</c> string for the VFS key. Versioned so a future
    /// rotation policy can derive under a new context without colliding
    /// with the cached entry.
    /// </summary>
    public const string VfsHkdfContext = "sqlite-vfs:globalKey:v1";

    public partial EncryptedDiskLifecycle(
        AuthenticationModel auth,
        IEncryptedSqliteWasmDatabaseService session,
        IPrfService prfService,
        ISecureKeyCache keyCache);

    /// <summary>
    /// Auto-detected internal observer — accesses <c>Auth.PublicKey</c>
    /// in the body, so the SG wires this method to fire whenever
    /// <see cref="AuthenticationModel.PublicKey"/> changes. Identity set
    /// (sign-in succeeded) drives Unlock; identity cleared (sign-out / TTL
    /// elapse) drives Lock.
    /// </summary>
    private async Task OnAuthPublicKeyChangedAsync(CancellationToken cancellationToken)
    {
        var hasIdentity = !string.IsNullOrEmpty(Auth.PublicKey);
        if (hasIdentity)
        {
            await UnlockIfNeededAsync(cancellationToken);
        }
        else
        {
            await LockIfUnlockedAsync(cancellationToken);
        }
    }

    private async ValueTask UnlockIfNeededAsync(CancellationToken cancellationToken)
    {
        var vfs = await Session.GetStateAsync(cancellationToken);
        if (!vfs.Encrypted || vfs.Unlocked)
        {
            return;
        }

        var derive = await PrfService.DeriveDomainKeyAsync(VfsDomainId, VfsHkdfContext);
        if (!derive.Success || derive.Value is null)
        {
            return;
        }

        var keyBytes = KeyCache.TryGet(derive.Value);
        if (keyBytes is null || keyBytes.Length != 32)
        {
            return;
        }

        try
        {
            await Session.UnlockAsync(keyBytes, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private async ValueTask LockIfUnlockedAsync(CancellationToken cancellationToken)
    {
        var vfs = await Session.GetStateAsync(cancellationToken);
        if (vfs.Unlocked)
        {
            await Session.LockAsync(cancellationToken);
        }
    }
}
