namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Single-row table storing the Admin's Ed25519 signature over the canonical
/// SHA-256 hash of all <see cref="SyncPermission"/> rows. Verified by the
/// worker at startup and before every import to ensure the permission table
/// hasn't been tampered with.
/// </summary>
public sealed class PermissionTableSignature
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 hash of the canonical permission table representation.</summary>
    public required byte[] PermissionHash { get; set; }

    /// <summary>Admin's Ed25519 signature over <see cref="PermissionHash"/>.</summary>
    public required byte[] AdminSignature { get; set; }

    /// <summary>Admin's Ed25519 public key (Base64) — for worker-side verification.</summary>
    public required string AdminEd25519PublicKey { get; set; }
}
