namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Roles for sharing participants within a scope (ShareTarget.Role).
/// Admin is NOT a sync role — it's a system-level flag on TrustedContact.
/// Owner = the sharer who created the scope, always has full control.
/// </summary>
public enum SyncRole
{
    /// <summary>Full control over the shared scope. Automatically assigned to creator.</summary>
    OWNER = 0,

    /// <summary>Read + write by default; concrete CRUD comes from <c>[Permissions]</c> attribute slots on the entity.</summary>
    EDITOR = 1,

    /// <summary>Default read access; concrete CRUD comes from <c>[Permissions]</c> attribute slots on the entity. Column overrides via <c>[AllowUpdate]</c>/<c>[DenyUpdate]</c>.</summary>
    VIEWER = 2
}

/// <summary>
/// Per-row operation kind, derived locally inside the worker during delta apply.
/// Op-kind is never shipped in the encrypted envelope — the worker computes it from
/// row state (tombstone column for Delete, PK presence for Insert vs Update).
/// </summary>
public enum SyncOperation
{
    INSERT = 0,
    UPDATE = 1,
    DELETE = 2
}
