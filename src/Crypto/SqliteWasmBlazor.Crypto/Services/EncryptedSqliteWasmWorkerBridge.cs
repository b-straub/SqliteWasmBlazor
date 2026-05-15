// SqliteWasmBlazor.Crypto — Plane 2 worker bridge wrapper.
// MIT License

using System.Security.Cryptography;
using MessagePack;

namespace SqliteWasmBlazor.Crypto.Services;

/// <summary>
/// Plane 2 worker-bridge wrapper. Encapsulates encryption-specific worker
/// dispatch (set/clear global key, in-place encrypt/decrypt, disk-manifest
/// read/write/clear), delta-sync ops (export/import/rotate), and the
/// mode-aware import/export-rekey paths that consume a 32-byte key.
///
/// <para>
/// Delegates to plane-1's <see cref="SqliteWasmWorkerBridge"/> for every
/// binary-payload worker round-trip via its internal helpers
/// (<c>PostBinaryAsync</c> / <c>PostBinaryForBytesAsync</c> / etc., added in
/// plane-split Phase 2a). The plane-1 bridge's private state — pending-request
/// maps, request-id allocator, JSImport partials, open-database mirror — stays
/// private to plane 1; this wrapper never reaches into it directly.
/// </para>
///
/// <para>
/// Singleton accessible via <see cref="Instance"/>. The internal-only
/// constructor takes a plane-1 bridge for DI / test composition; production
/// callers use <c>Instance</c> which wraps <c>SqliteWasmWorkerBridge.Instance</c>.
/// </para>
/// </summary>
internal sealed partial class EncryptedSqliteWasmWorkerBridge
{
    private static readonly Lazy<EncryptedSqliteWasmWorkerBridge> _instance =
        new(() => new EncryptedSqliteWasmWorkerBridge(SqliteWasmWorkerBridge.Instance));

    /// <summary>Singleton wrapper around the plane-1 bridge singleton.</summary>
    public static EncryptedSqliteWasmWorkerBridge Instance => _instance.Value;

    private readonly SqliteWasmWorkerBridge _bridge;

    internal EncryptedSqliteWasmWorkerBridge(SqliteWasmWorkerBridge bridge)
    {
        _bridge = bridge;
    }

    /// <summary>
    /// Worker-bridge primitive: install the global encryption key. The
    /// worker's <c>setGlobalEncryptionKeyOp</c> closes every cached DB before
    /// swapping <c>globalKey</c> for page-cache coherence at the session
    /// boundary — SQLite caches plaintext pages after first read; without the
    /// close, K_old plaintext could be served to a K_new session (or vice
    /// versa). The worker pass is authoritative; this bridge pre-pass keeps
    /// the C# mirror consistent.
    /// NOT public — production callers go through
    /// <see cref="IEncryptedSqliteWasmDatabaseService.UnlockAsync"/>.
    /// </summary>
    internal async Task SetEncryptionKeyAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException(
                $"key must be exactly 32 bytes, got {key.Length}", nameof(key));
        }

        await _bridge.CloseAllOpenDatabasesAsync(cancellationToken);

        var header = new VfsKeyHeader
        {
            Version = 1,
            Key = key.ToArray(),
            AadVersion = "v1",
        };
        var envelope = MessagePackSerializer.Serialize(header);
        try
        {
            await _bridge.PostBinaryAsync(
                new { type = "setGlobalEncryptionKey" },
                envelope,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
            header.Clear();
        }
    }

    /// <summary>
    /// Worker-bridge primitive: drop the global encryption key. Same close-
    /// pass-for-cache-coherence rationale as <see cref="SetEncryptionKeyAsync"/>;
    /// the worker's <c>clearGlobalEncryptionKeyOp</c> independently closes its
    /// <c>openDatabases</c> map, this bridge pre-pass keeps the C# mirror in
    /// sync. NOT public — production callers go through
    /// <see cref="IEncryptedSqliteWasmDatabaseService.LockAsync"/> /
    /// <c>LeaveEncryptedAsync</c> / <c>ResetDiskAsync</c>.
    /// </summary>
    internal async Task ClearEncryptionKeyAsync(CancellationToken cancellationToken = default)
    {
        await _bridge.CloseAllOpenDatabasesAsync(cancellationToken);
        var request = new { type = "clearGlobalEncryptionKey" };
        await _bridge.SendRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Per-DB crypto primitive — internal only. Production callers go through
    /// <see cref="IEncryptedSqliteWasmDatabaseService.EnterEncryptedAsync"/>
    /// (which loops over <see cref="ISqliteWasmDatabaseService.ListDatabasesAsync"/>).
    /// </summary>
    internal async Task EncryptDatabaseInPlaceAsync(
        string databaseName,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException(
                $"key must be exactly 32 bytes, got {key.Length}", nameof(key));
        }

        var header = new VfsKeyHeader
        {
            Version = 1,
            Key = key.ToArray(),
            AadVersion = "v1",
        };
        var envelope = MessagePackSerializer.Serialize(header);
        try
        {
            var result = await _bridge.PostBinaryAsync(
                new { type = "encryptDb", database = databaseName },
                envelope,
                cancellationToken);

            if (result.RowsAffected != 0)
            {
                throw new InvalidOperationException(
                    $"Worker returned unexpected in-place rekey outcome code {result.RowsAffected}");
            }
            // Worker closes the DB during in-place conversion for a stable
            // OPFS snapshot; force the next DbContext open to re-enter xOpen.
            _bridge.MarkDatabaseClosed(databaseName);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
            header.Clear();
        }
    }

    /// <summary>
    /// Per-DB crypto primitive — internal only. Production callers go through
    /// <see cref="IEncryptedSqliteWasmDatabaseService.LeaveEncryptedAsync"/>.
    /// </summary>
    internal async Task DecryptDatabaseInPlaceAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        // No key payload — worker uses its currently-registered K. The caller
        // is responsible for SetEncryptionKeyAsync(K_old) first.
        var request = new { type = "decryptDb", database = databaseName };
        var result = await _bridge.SendRequestAsync(request, cancellationToken);
        if (result.RowsAffected != 0)
        {
            throw new InvalidOperationException(
                $"Worker returned unexpected in-place decrypt outcome code {result.RowsAffected}");
        }
        _bridge.MarkDatabaseClosed(databaseName);
    }

    /// <summary>
    /// Read the disk-bound passkey manifest. Walks every DB in the SAHPool,
    /// pulls bytes 524..1023 of each header sector, and returns a typed state
    /// plus the (optional) body bytes.
    /// </summary>
    /// <param name="verifyMac">When <c>true</c>, the worker recomputes the
    /// HMAC under the currently-installed globalKey and compares. Pre-unlock
    /// callers (auth-flow fast-fail) leave it false and rely on the body bytes
    /// alone.</param>
    internal async Task<(string state, byte[]? body, int? schemaVersion)>
        ReadDiskManifestAsync(bool verifyMac, CancellationToken cancellationToken = default)
    {
        var request = new { type = "readDiskManifest", verifyMac };
        var result = await _bridge.SendRequestAsync(request, cancellationToken);
        var state = result.ManifestState ?? "absent";
        var body = !string.IsNullOrEmpty(result.ManifestBody)
            ? Convert.FromBase64String(result.ManifestBody)
            : null;
        return (state, body, result.ManifestSchemaVersion);
    }

    /// <summary>
    /// Write <paramref name="body"/> as the manifest body for every DB in the
    /// SAHPool. The worker derives the HMAC key from the currently-installed
    /// globalKey, builds the 500-byte region (magic + version + length + body
    /// + pad + HMAC) and writes it to bytes 524..1023 of each DB's header
    /// sector. Caller must ensure the disk is Encrypted+Unlocked.
    /// </summary>
    internal async Task WriteDiskManifestAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var bodyArray = body.ToArray();
        try
        {
            await _bridge.PostBinaryAsync(
                new { type = "writeDiskManifest" },
                bodyArray,
                cancellationToken);
        }
        finally
        {
            // Body contents are not secret (credentialId, pubkey fingerprint —
            // both publicly disclosable), but mirror the same hygiene
            // discipline the rest of the bridge uses for transmitted blobs.
            CryptographicOperations.ZeroMemory(bodyArray);
        }
    }

    /// <summary>
    /// Zero bytes 524..1023 of every DB's header sector. Idempotent — called
    /// by <c>LeaveEncryptedAsync</c> / <c>ResetDiskAsync</c>.
    /// </summary>
    internal async Task ClearDiskManifestAsync(CancellationToken cancellationToken = default)
    {
        var request = new { type = "clearDiskManifest" };
        await _bridge.SendRequestAsync(request, cancellationToken);
    }
}
