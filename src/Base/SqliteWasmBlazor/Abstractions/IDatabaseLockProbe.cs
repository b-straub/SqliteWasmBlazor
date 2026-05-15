// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Plane-1-facing probe for the encrypted-VFS lock state. Implemented by
/// plane 2 (SqliteWasmBlazor.Crypto)'s encryption lifecycle service; resolved
/// optionally by <see cref="SqliteWasmServiceCollectionExtensions.InitializeSqliteWasmDatabaseAsync{TContext}"/>
/// so plain-only consumers (no plane 2 registration) skip the check.
///
/// <para>
/// Kept narrow on purpose — only the boot-state probe lives here. Full
/// lifecycle (Unlock / Lock / EnterEncrypted / etc.) is plane 2's
/// <c>IEncryptedSqliteWasmDatabaseService</c>, which plane 1 must not see.
/// </para>
/// </summary>
internal interface IDatabaseLockProbe
{
    Task<DatabaseLockState> GetStateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Plane-1 view of the encrypted-VFS state — just the three fields the
/// plane-1 init flow needs to decide whether to report
/// <see cref="DbInitState.ENCRYPTED_LOCKED"/>. Plane 2 owns the richer
/// <c>EncryptedDiskState</c> with its lifecycle implications.
/// </summary>
internal sealed record DatabaseLockState(bool Encrypted, bool Unlocked, string? Hint);
