namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Trust level for a contact (PGP-style).
/// </summary>
public enum TrustLevel
{
    None = 0,
    Marginal = 1,
    Full = 2
}

/// <summary>
/// Direction of trust establishment.
/// </summary>
public enum TrustDirection
{
    Sent = 0,
    Received = 1
}

/// <summary>
/// Status of a sent invitation.
/// </summary>
public enum InviteStatus
{
    Pending = 0,
    Accepted = 1,
    Expired = 2,
    Revoked = 3
}

/// <summary>
/// Roles for sharing participants.
/// Admin is NOT a sync role — it's a system-level instance manager.
/// Owner = the sharer who created the scope, always has full control.
/// </summary>
public enum SyncRole
{
    /// <summary>Full control over the shared scope. Automatically assigned to creator.</summary>
    Owner = 0,

    /// <summary>Read + write by default; concrete CRUD comes from <c>[Permissions]</c> attribute slots on the entity.</summary>
    Editor = 1,

    /// <summary>Default read access; concrete CRUD comes from <c>[Permissions]</c> attribute slots on the entity. Column overrides via <c>[AllowUpdate]</c>/<c>[DenyUpdate]</c>.</summary>
    Viewer = 2
}

/// <summary>
/// Per-row operation kind, derived locally inside the worker during delta apply.
/// Op-kind is never shipped in the encrypted envelope — the worker computes it from
/// row state (tombstone column for Delete, PK presence for Insert vs Update).
/// </summary>
public enum SyncOperation
{
    Insert = 0,
    Update = 1,
    Delete = 2
}
