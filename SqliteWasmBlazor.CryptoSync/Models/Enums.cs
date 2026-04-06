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

    /// <summary>Read + write per permission template (admin-defined).</summary>
    Editor = 1,

    /// <summary>Read-only, column overrides possible (admin-defined).</summary>
    Viewer = 2
}
