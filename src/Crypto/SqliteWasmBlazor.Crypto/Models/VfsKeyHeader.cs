using MessagePack;
using System.Security.Cryptography;

namespace SqliteWasmBlazor;

/// <summary>
/// Envelope carrying the PRF-derived VFS key from C# to the worker on DB open.
/// Versioned MessagePack type mirroring the shape of <c>CryptoHeader</c> so
/// the C# → worker contract is uniform across all paths that ship key material:
/// the delta export/import path and this open-with-key path.
///
/// <para>
/// <b>Security note.</b> <see cref="Key"/> is sensitive. Callers must call
/// <see cref="Clear"/> in a <c>finally</c> block after the worker call returns.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class VfsKeyHeader
{
    /// <summary>Wire format version. Bumped on schema changes.</summary>
    [Key(0)]
    public int Version { get; set; } = 1;

    /// <summary>
    /// The 32-byte ChaCha20-Poly1305 key used by the PRF-keyed VFS to
    /// encrypt/decrypt every page of this database. Derived by the caller
    /// via HKDF from a PRF seed.
    /// </summary>
    [Key(1)]
    public byte[] Key { get; set; } = [];

    /// <summary>
    /// AAD prefix version expected by the worker. Must match the VFS fork's
    /// internal constant (currently <c>"v1"</c>). Stored here so a future
    /// AAD format bump can be coordinated between C# and worker without a
    /// wire-compat break.
    /// </summary>
    [Key(2)]
    public string AadVersion { get; set; } = "v1";

    /// <summary>
    /// Zero the key buffer. Call in a <c>finally</c> block after the worker
    /// call returns.
    /// </summary>
    public void Clear()
    {
        if (Key.Length > 0)
        {
            CryptographicOperations.ZeroMemory(Key);
        }
    }
}
