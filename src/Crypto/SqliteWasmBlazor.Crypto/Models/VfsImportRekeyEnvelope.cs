using MessagePack;
using System.Security.Cryptography;

namespace SqliteWasmBlazor;

/// <summary>
/// Wire envelope for the worker's <c>importDbRekey</c> path. Carries both
/// the source decryption key (<see cref="WrapKey"/>) and the page-ciphertext
/// payload (<see cref="DbBytes"/>) in a single MessagePack frame so the
/// existing single-binary postMessage transport stays unchanged.
///
/// <para>
/// The worker decrypts <see cref="DbBytes"/> page-by-page under
/// <see cref="WrapKey"/>, re-encrypts under the registered globalKey via
/// the existing <c>rekeySlots</c> primitive, then writes the result with
/// the standard verify-on-write flow. The recipient's disk-globalKey thus
/// stays bound to the recipient's PRF-derived VFS key — the wrap key is a
/// transit-only secret.
/// </para>
///
    /// <para>
    /// <b>Security.</b> <see cref="WrapKey"/> is the per-export ECIES-unwrapped
    /// content key; treat exactly like <see cref="VfsKeyHeader.Key"/>. This DTO
    /// references caller-owned buffers, so <see cref="Clear"/> intentionally does
    /// not zero the fields. The bridge must zero the serialized MessagePack
    /// envelope bytes, and the outer import flow owns the shared wrap-key buffer.
    /// </para>
/// </summary>
[MessagePackObject]
public sealed class VfsImportRekeyEnvelope
{
    /// <summary>Wire format version. Bumped on schema changes.</summary>
    [Key(0)]
    public int Version { get; set; } = 1;

    /// <summary>
    /// 32-byte ChaCha20-Poly1305 source key — the per-export wrap key the
    /// sender used in the asymmetric export. Recipient derives this by
    /// ECIES-unwrapping the envelope's <c>WrappedContentKey</c>.
    /// </summary>
    [Key(1)]
    public byte[] WrapKey { get; set; } = [];

    /// <summary>
    /// Page ciphertext under <see cref="WrapKey"/> for one DB. The worker
    /// rekeys these slots from <see cref="WrapKey"/> to the registered
    /// globalKey before persisting via the standard import path.
    /// </summary>
    [Key(2)]
    public byte[] DbBytes { get; set; } = [];

    /// <summary>
    /// AAD prefix version that bound the page ciphertext at export time.
    /// Must match the worker's <c>buildPageAad</c> constant
    /// (currently <c>"v1"</c>). Held alongside the key so a future AAD
    /// bump is wire-coordinated, not implicit.
    /// </summary>
    [Key(3)]
    public string AadVersion { get; set; } = "v1";

    /// <summary>
    /// No-op. <see cref="WrapKey"/> is assigned by REFERENCE from a
    /// caller-owned buffer (typically the outer
    /// <c>ImportDiskAsync</c>'s loop variable). Zeroing here would
    /// stomp on subsequent loop iterations — observed bug:
    /// iteration 1 succeeds, iteration 2 sees wrapKey.fp=00000000.
    /// The outer caller owns the wrapKey lifecycle and zeros it once
    /// after the loop completes (single shared buffer for every file).
    /// <see cref="DbBytes"/> is ciphertext (not secret on its own).
    /// </summary>
    public void Clear()
    {
        // intentionally empty — see XML doc above.
    }
}
