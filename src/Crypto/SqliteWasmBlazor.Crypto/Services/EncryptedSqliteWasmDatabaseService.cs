// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MessagePack;
using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor;

/// <summary>
/// Production implementation of <see cref="IEncryptedSqliteWasmDatabaseService"/>.
/// Composes the worker bridge primitives (<c>SetEncryptionKeyAsync</c> /
/// <c>ClearEncryptionKeyAsync</c> / <c>ListDatabasesAsync</c> / per-DB
/// encrypt-in-place / decrypt-in-place / export) into the session-shaped
/// lifecycle and the four boundary scenarios.
///
/// <para>
/// <b>Encrypted-state signal.</b> The disk-bound passkey manifest
/// (bytes 524..1023 of every SAH slot's plaintext header sector — see
/// <c>worker/vfs-prf/manifest.ts</c>) is the source of truth for whether
/// the VFS is encrypted. Manifest present on every DB in the pool ⇒
/// Plain → Encrypted transition completed ⇒ DBs on disk are ciphertext
/// under the PRF-derived key. Manifest absent ⇒ VFS is plain. Co-located
/// with the SAHPool slots so it cannot drift from the disk state.
/// </para>
///
/// <para>
/// Singleton — registered by <c>AddSqliteWasmBlazorCrypto()</c> (lives
/// alongside <c>IPrfService</c> because <see cref="ResetPoolAsync"/>
/// implicitly clears the PRF cache to keep the auth UI in lockstep with
/// the disk's Plain transition). Tracks <see cref="_isUnlocked"/> in
/// memory to avoid an extra worker round-trip in
/// <see cref="GetStateAsync"/>.
/// </para>
/// </summary>
internal sealed class EncryptedSqliteWasmDatabaseService
    : IEncryptedSqliteWasmDatabaseService, IDatabaseLockProbe
{
    private readonly SqliteWasmWorkerBridge _bridge;
    private readonly EncryptedSqliteWasmWorkerBridge _encryptedBridge;
    private readonly IDbInitializationReporter _reporter;
    private readonly IDbSchemaInitializer _schema;
    private readonly IDbInitializationStatus _status;
    private readonly IPrfService _prfService;
    private readonly ICryptoProvider _cryptoProvider;
    private bool _isUnlocked;

    /// <summary>
    /// CredentialId that EnterEncryptedAsync intends to bind the disk to.
    /// Held in memory across an Encrypted+Unlocked session so we can auto-
    /// heal the manifest when the pool was empty at EnterEncrypted time
    /// (no DBs yet → nothing to write into) and DBs are subsequently
    /// created — typically via DbContext.EnsureCreatedAsync. Cleared on
    /// Leave/Reset.
    /// </summary>
    private string? _expectedCredentialId;

    public EncryptedSqliteWasmDatabaseService(
        IDbInitializationReporter reporter,
        IDbInitializationStatus status,
        IPrfService prfService,
        ICryptoProvider cryptoProvider,
        IDbSchemaInitializer schema)
    {
        _bridge = SqliteWasmWorkerBridge.Instance;
        _encryptedBridge = EncryptedSqliteWasmWorkerBridge.Instance;
        _reporter = reporter;
        _status = status;
        _prfService = prfService;
        _cryptoProvider = cryptoProvider;
        _schema = schema;
    }

    /// <summary>
    /// Bridge between the encrypted-VFS session lifecycle and
    /// <see cref="IDbInitializationStatus"/>: the
    /// <c>PrfAuthenticationStateProvider</c> emits a
    /// <c>DatabaseState=OPEN/LOCKED</c> claim from this state, and the
    /// <c>DatabaseOpen</c> AuthorizeView policy gates page content on it.
    /// Unlock/Lock/Reset must report through here so every &lt;AuthorizeView&gt;
    /// in the tree re-evaluates without a manual UI poke.
    /// </summary>
    private void ReportDbState(DbInitState state, IDbInitFailure? failure = null)
    {
        // Don't downgrade out of a hard-stop boot diagnosis (TAB_LOCKED,
        // SCHEMA_INCOMPATIBLE, FAILED, TIMEOUT) — those need user action
        // beyond unlock/lock and shouldn't be silently overwritten.
        if (_status.State is DbInitState.TAB_LOCKED
            or DbInitState.SCHEMA_INCOMPATIBLE
            or DbInitState.TIMEOUT
            or DbInitState.FAILED)
        {
            return;
        }

        _reporter.Report(state, failure);
    }

    // IDatabaseLockProbe — plane-1-facing minimal probe so
    // InitializeSqliteWasmDatabaseAsync<TContext> can detect ENCRYPTED_LOCKED
    // boot state without referencing plane-2 types. Maps the rich
    // EncryptedPoolState down to the three fields plane 1 cares about.
    async Task<DatabaseLockState> IDatabaseLockProbe.GetStateAsync(CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken);
        return new DatabaseLockState(state.Encrypted, state.Unlocked, state.Hint);
    }

    public async Task<EncryptedPoolState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        // Manifest is the source of truth for the Encrypted/Plain axis.
        // Disk-as-unit invariant guarantees one shared manifest across
        // every DB; the worker side asserts equality on read.
        var (state, credentialId) = await ReadManifestAsync(verifyMac: _isUnlocked, cancellationToken);

        switch (state)
        {
            case ManifestState.PRESENT:
                _expectedCredentialId = credentialId;
                _bridge.SetPoolLocked(!_isUnlocked);
                return new EncryptedPoolState(true, _isUnlocked, credentialId);

            case ManifestState.ABSENT:
                return await ProbeAbsentManifestAsync(cancellationToken);

            case ManifestState.MISMATCH:
                // Mismatch can arise legitimately when a new DB is created
                // post-EnterEncrypted while another DB already carries the
                // manifest (the worker doesn't auto-stamp new SAH slots).
                // If we know who owns this disk and the worker still holds
                // the global key, re-flush the manifest across every DB to
                // restore the disk-as-unit invariant. If we don't, the
                // disk is genuinely inconsistent — surface as Encrypted+
                // Locked with no hint so the UI offers a reset.
                if (_expectedCredentialId is { Length: > 0 } expectedForHeal
                    && _isUnlocked)
                {
                    await WriteManifestAsync(expectedForHeal, cancellationToken);
                    _bridge.SetPoolLocked(false);
                    return new EncryptedPoolState(true, true, expectedForHeal);
                }

                _isUnlocked = false;
                _bridge.SetPoolLocked(true);
                return new EncryptedPoolState(true, false, null);

            case ManifestState.MALFORMED:
            case ManifestState.TAMPERED:
                // Surface corruption as Encrypted+Locked with no hint —
                // UI sees the auth panel + reset escape hatch but can't
                // route to a specific passkey because the manifest can't
                // be trusted. Caller's only recovery is ResetPoolAsync.
                _isUnlocked = false;
                _bridge.SetPoolLocked(true);
                return new EncryptedPoolState(true, false, null);

            default:
                throw new InvalidOperationException(
                    $"Unhandled disk manifest state '{state}'.");
        }
    }

    /// <summary>
    /// Manifest absent — two cases:
    ///   1. Empty-pool Encrypted+Unlocked — EnterEncryptedAsync was called
    ///      while the pool had no DBs (so the manifest write was a no-op),
    ///      and we still hold the credentialId in memory. Now that a DB
    ///      may exist (e.g. <c>DbContext.EnsureCreatedAsync</c> just ran),
    ///      flush the manifest into it.
    ///   2. Genuine Plain — no manifest, no expected credentialId.
    /// </summary>
    private async Task<EncryptedPoolState> ProbeAbsentManifestAsync(CancellationToken cancellationToken)
    {
        if (_expectedCredentialId is { Length: > 0 } expected && _isUnlocked)
        {
            var dbs = await _bridge.ListDatabasesAsync(cancellationToken);
            if (dbs.Count > 0)
            {
                await WriteManifestAsync(expected, cancellationToken);
            }

            _bridge.SetPoolLocked(false);
            return new EncryptedPoolState(true, true, expected);
        }

        _isUnlocked = false;
        _bridge.SetPoolLocked(false);
        return EncryptedPoolState.Plain;
    }

    /// <summary>
    /// Read the disk manifest via the worker bridge and unpack the
    /// MessagePack body. Returns <see cref="ManifestState.PRESENT"/> +
    /// the credentialId when the manifest is intact; other states are
    /// reported with a null credentialId (caller branches on state).
    /// </summary>
    private async Task<(ManifestState State, string? CredentialId)> ReadManifestAsync(
        bool verifyMac, CancellationToken cancellationToken)
    {
        var (raw, body, _) = await _encryptedBridge.ReadPoolManifestAsync(verifyMac, cancellationToken);
        var state = raw switch
        {
            "absent" => ManifestState.ABSENT,
            "present" => ManifestState.PRESENT,
            "mismatch" => ManifestState.MISMATCH,
            "tampered" => ManifestState.TAMPERED,
            "malformed" => ManifestState.MALFORMED,
            _ => throw new InvalidOperationException(
                $"Worker returned unexpected disk-manifest state '{raw}'."),
        };
        if (state != ManifestState.PRESENT || body is null)
        {
            return (state, null);
        }

        PoolManifestBody decoded;
        try
        {
            decoded = MessagePackSerializer.Deserialize<PoolManifestBody>(body);
        }
        catch (MessagePackSerializationException)
        {
            // Body bytes don't deserialize even though magic + HMAC checked
            // out — surface as malformed (probably a future-schema body
            // landed under a current-schema parser).
            return (ManifestState.MALFORMED, null);
        }

        return (ManifestState.PRESENT, decoded.CredentialId ?? string.Empty);
    }

    /// <summary>
    /// Build the MessagePack body for <paramref name="credentialId"/>
    /// (diagnostic fingerprint left empty — not load-bearing for unlock)
    /// and ship it to the worker, which derives the manifest MAC key from
    /// the active globalKey and writes the 500-byte region into every DB
    /// in the pool.
    /// </summary>
    private Task WriteManifestAsync(string credentialId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(credentialId))
        {
            throw new ArgumentException(
                "credentialId must be non-empty when writing the disk manifest.",
                nameof(credentialId));
        }

        var body = new PoolManifestBody
        {
            CredentialId = credentialId,
            PublicKeyFingerprint = string.Empty,
        };
        var bytes = MessagePackSerializer.Serialize(body);
        return _encryptedBridge.WritePoolManifestAsync(bytes, cancellationToken);
    }

    public async Task UnlockAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException(
                $"key must be exactly 32 bytes, got {key.Length}", nameof(key));
        }

        // State-aware. Mirrors LockAsync's plain-disk handling.
        //   - Encrypted disk: install + verify manifest MAC under the key.
        //     A wrong key trips VerifyUnlockedManifestAsync, which clears
        //     globalKey, drops back to Encrypted+Locked, and throws. This
        //     is the security improvement from the manifest-MAC audit.
        //   - Plain disk (no manifest yet): UnlockAsync is being used as
        //     an "install globalKey primitive" — by test fixtures, by the
        //     synthetic-PRF-seed paths, and by any pre-EnterEncrypted
        //     setup. Install silently and skip verify; there's nothing
        //     to verify against. The MAC-bound security guarantee for
        //     the encrypted-disk path is preserved.
        var pre = await GetStateAsync(cancellationToken);
        await InstallEncryptionKeyAsync(key, cancellationToken);
        if (pre.Encrypted)
        {
            await VerifyUnlockedManifestAsync(allowAbsentForEmptyPool: false, cancellationToken);
        }

        // Boot could not migrate this pool: its pages were ciphertext and the
        // key did not exist yet. It does now, so the step boot registered runs
        // here — and reports READY itself, or the failure that stopped it.
        await _schema.EnsureSchemaAsync(cancellationToken);
    }

    private async Task InstallEncryptionKeyAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        // Disk-as-unit: install globalKey in the worker and release the
        // bridge gate so DB ops route through the encrypted hot path.
        await _encryptedBridge.SetEncryptionKeyAsync(key, cancellationToken);
        _isUnlocked = true;
        _bridge.SetPoolLocked(false);
    }

    private async Task VerifyUnlockedManifestAsync(
        bool allowAbsentForEmptyPool,
        CancellationToken cancellationToken)
    {
        var (state, credentialId) = await ReadManifestAsync(verifyMac: true, cancellationToken);
        switch (state)
        {
            case ManifestState.PRESENT:
                _expectedCredentialId = credentialId;
                return;

            case ManifestState.ABSENT when allowAbsentForEmptyPool:
                return;

            default:
                await _encryptedBridge.ClearEncryptionKeyAsync(cancellationToken);
                _isUnlocked = false;
                _bridge.SetPoolLocked(state != ManifestState.ABSENT);
                ReportDbState(
                    state == ManifestState.ABSENT ? DbInitState.READY : DbInitState.ENCRYPTED_LOCKED,
                    state == ManifestState.ABSENT
                        ? null
                        : new EncryptedDatabaseLockedFailure(string.Empty, string.Empty));
                throw new InvalidOperationException(
                    $"Unlock rejected: disk manifest did not verify under the supplied key (state={state}).");
        }
    }

    public async Task LockAsync(CancellationToken cancellationToken = default)
    {
        // State-aware lock. Three possible starting points:
        //   - Encrypted+Unlocked: real lock transition. Engage gate FIRST so
        //     in-flight or post-clear DB ops fail with PoolLockedException
        //     instead of reading ciphertext as plain. Then drop globalKey
        //     and report ENCRYPTED_LOCKED so the AuthorizeView gate flips.
        //   - Plain (no manifest): callers (typically test fixtures) use Lock
        //     as a "drop any stray globalKey" primitive. Honor that
        //     semantically: clear key, but DO NOT engage the gate (would
        //     break plain ops) and DO NOT report ENCRYPTED_LOCKED.
        //   - Encrypted+Locked already: drop the (empty) key idempotently and
        //     re-engage the gate; nothing else to do.
        var state = await GetStateAsync(cancellationToken);
        var encrypted = state.Encrypted;

        if (encrypted)
        {
            _bridge.SetPoolLocked(true);
        }

        await _encryptedBridge.ClearEncryptionKeyAsync(cancellationToken);
        _isUnlocked = false;

        if (encrypted)
        {
            ReportDbState(
                DbInitState.ENCRYPTED_LOCKED,
                new EncryptedDatabaseLockedFailure(string.Empty, state.Hint ?? string.Empty));
        }
    }

    public async Task EnterEncryptedAsync(
        ReadOnlyMemory<byte> key,
        string credentialId,
        CancellationToken cancellationToken = default)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException(
                $"key must be exactly 32 bytes, got {key.Length}", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(credentialId))
        {
            throw new ArgumentException(
                "credentialId must be non-empty (Base64 WebAuthn credential id from Register).",
                nameof(credentialId));
        }

        var current = await GetStateAsync(cancellationToken);
        if (current.Encrypted)
        {
            throw new PoolOperationRejectedException(
                PoolOperationRejection.ENTER_NEEDS_PLAIN,
                "EnterEncryptedAsync requires EncryptedPoolState.Plain — VFS is already encrypted.");
        }

        var databases = await _bridge.ListDatabasesAsync(cancellationToken);
        var encryptedSoFar = new List<string>(databases.Count);
        try
        {
            // Phase 1: install the global key BEFORE the encrypt loop.
            // Install-K-first ordering avoids the managed byte[] backup of
            // every plain DB that the pre-G7 path did — a 247 MB DB no
            // longer OOMs Mobile Safari before the first worker call.
            // Mid-loop failure rolls back via per-DB decrypt-in-place
            // under the still-registered K, so the crash-safety contract
            // matches the byte[] backup era with zero managed allocation
            // per DB.
            await InstallEncryptionKeyAsync(key, cancellationToken);

            // Phase 2: walk every plain DB in OPFS, encrypt-in-place under K.
            // EncryptDatabaseInPlaceAsync is per-DB; the worker closes each
            // DB during the conversion, so OFile state can't leak. Track
            // encryptedSoFar so a later failure can decrypt back exactly
            // the DBs that crossed the cipher boundary.
            foreach (var db in databases)
            {
                await _encryptedBridge.EncryptDatabaseInPlaceAsync(db, key, cancellationToken);
                encryptedSoFar.Add(db);
            }

            // Phase 3: write the disk-bound manifest as the last atomic step.
            // GetStateAsync flips to Encrypted+Unlocked only after every DB has
            // been re-wrapped, the worker holds the key, AND the manifest has
            // been recorded onto every DB's header sector. When the pool is
            // empty (caller invokes EnterEncryptedAsync before any DB exists),
            // the worker write is a no-op; we still cache _expectedCredentialId
            // so the next GetStateAsync auto-heals once a DB has been created.
            _expectedCredentialId = credentialId;
            await WriteManifestAsync(credentialId, cancellationToken);
            await VerifyUnlockedManifestAsync(
                allowAbsentForEmptyPool: databases.Count == 0,
                cancellationToken);
        }
        catch
        {
            await RollBackEnterEncryptedAsync(encryptedSoFar, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Roll back a partial Plain → Encrypted transition without managed-heap
    /// snapshots. Decrypts every DB that crossed the cipher boundary (in
    /// reverse install order) via the same chunked decrypt-in-place primitive
    /// used by LeaveEncryptedAsync. Clears the manifest (which may have been
    /// partially written if the failure was post-encrypt) and drops the
    /// installed key. After this the disk is back to Plain.
    /// </summary>
    private async Task RollBackEnterEncryptedAsync(
        IReadOnlyList<string> encryptedSoFar,
        CancellationToken cancellationToken)
    {
        // Decrypt under the still-registered globalKey first — the worker's
        // decryptDatabaseInPlace requires hasGlobalKey() and snapshots from
        // it, so we must not clear the key until every DB is back to plain.
        for (var i = encryptedSoFar.Count - 1; i >= 0; i--)
        {
            await _encryptedBridge.DecryptDatabaseInPlaceAsync(encryptedSoFar[i], cancellationToken);
        }

        // Clear any manifest bytes that landed before the failure (the
        // primitive is a no-op when nothing was written).
        await _encryptedBridge.ClearPoolManifestAsync(cancellationToken);

        // Finally drop the key + reset in-memory state.
        await _encryptedBridge.ClearEncryptionKeyAsync(cancellationToken);
        _isUnlocked = false;
        _expectedCredentialId = null;
        _bridge.SetPoolLocked(false);
    }

    public async Task LeaveEncryptedAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetStateAsync(cancellationToken);
        if (!current.Encrypted || !current.Unlocked)
        {
            throw new PoolOperationRejectedException(
                PoolOperationRejection.LEAVE_NEEDS_UNLOCK,
                "LeaveEncryptedAsync requires Encrypted + Unlocked — call UnlockAsync first.");
        }

        // Phase 1: walk every encrypted DB, decrypt-in-place under the
        // active globalKey. DecryptDatabaseInPlaceAsync uses globalKey;
        // the worker closes each DB during the conversion.
        var databases = await _bridge.ListDatabasesAsync(cancellationToken);
        foreach (var db in databases)
        {
            await _encryptedBridge.DecryptDatabaseInPlaceAsync(db, cancellationToken);
        }

        // Phase 2: zero the manifest + drop globalKey. After this,
        // GetStateAsync returns Plain and the next boot proceeds without
        // an unlock prompt. Caller's responsibility to revoke the passkey
        // credential at the WebAuthn layer separately.
        await _encryptedBridge.ClearPoolManifestAsync(cancellationToken);
        await _encryptedBridge.ClearEncryptionKeyAsync(cancellationToken);
        _isUnlocked = false;
        _expectedCredentialId = null;
        _bridge.SetPoolLocked(false);
        ReportDbState(DbInitState.READY);
    }

    public async Task ResetPoolAsync(CancellationToken cancellationToken = default)
    {
        // Scorched-earth boundary: wipe the pool + drop the PRF cache.
        // PrfService.ClearKeys cascades through KeyExpired →
        // AuthenticationModel.OnSessionExpired → PublicKey=null →
        // PrfAuthenticationStateProvider → AuthorizeView re-evaluates.
        // Calling it here means a single Session.ResetPoolAsync() leaves
        // the whole encryption stack consistent — the auth flow doesn't
        // need to also be told "reset happened" by the orchestrating page.
        await WipePoolAsync(cancellationToken);
        _prfService.ClearKeys();
        ReportDbState(DbInitState.READY);
    }

    /// <summary>
    /// Drop globalKey, wipe every DB file from the pool, clear in-memory
    /// state. Does NOT clear the PRF cache — used by the guided-import flow
    /// which needs the PRF seed for the envelope's ECIES K_wrap unwrap
    /// after re-entering encrypted mode under the imported credential.
    /// </summary>
    private async Task WipePoolAsync(CancellationToken cancellationToken)
    {
        // Why delete the DB files (not just the manifest): EnterEncryptedAsync
        // walks every plain DB and runs encrypt-in-place. A pool that still
        // contains pre-Reset ciphertext slots fails the worker's plain-source
        // shape check ("not a multiple of 4096"); the user lands in a stuck
        // state where Reset → re-Encrypt is impossible without manual OPFS
        // surgery. Wiping the files makes Reset → re-Encrypt → Import the
        // canonical recipient flow for the asymmetric envelope.
        await _encryptedBridge.ClearEncryptionKeyAsync(cancellationToken);

        // Delete every DB file in the pool. The SAHPool delete path
        // explicitly zeros the reserved manifest sector before returning a
        // handle to the available pool, so a later plain import cannot inherit
        // a stale PFAM header. Ordering: globalKey is already dropped (above),
        // so the worker's deleteDatabase path runs without an active key —
        // pure file unlink, no AEAD verification. Snapshot the list before
        // looping because DeleteDatabaseAsync mutates the pool.
        var existing = await _bridge.ListDatabasesAsync(cancellationToken);
        foreach (var name in existing)
        {
            await _bridge.DeleteDatabaseAsync(name, cancellationToken);
        }

        _isUnlocked = false;
        _expectedCredentialId = null;
        _bridge.SetPoolLocked(false);
    }

    // ---------------------------------------------------------------------
    // Whole-disk export — disk-as-unit asymmetric (v3) envelope. Wraps a
    // fresh K_wrap via ECIES (X25519 ECDH + HKDF + AES-256-GCM) and rekeys
    // every page under K_wrap. Backup vs share is just "own pubkey vs peer
    // pubkey" at the call site — same code path. Streams the envelope to
    // a browser download Blob; C# never holds the envelope as a managed
    // byte[]. Mixed plain + encrypted DBs is not a representable disk
    // state, so the public surface only exposes whole-pool envelopes.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Streaming asymmetric disk export — assembles the v3 envelope as a
    /// virtual-concat Blob on the main thread and triggers the browser
    /// download directly. C# never sees a managed <c>byte[]</c> of the
    /// envelope; worker chunks the per-DB rekey, bridge composes the Blob,
    /// anchor click fires the save.
    /// </summary>
    public async Task ExportPoolToPubkeyAndDownloadAsync(
        string filename,
        string recipientX25519PublicKeyBase64,
        string recipientCredentialId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            throw new ArgumentException(
                "filename must be a non-empty string.", nameof(filename));
        }

        var ok = await WithPubkeyExportEnvelopeAsync(
            recipientX25519PublicKeyBase64,
            recipientCredentialId,
            (metadataJson, wrapKey) => SqliteWasmWorkerBridge.ExportPoolToDownloadAsync(
                filename, metadataJson, wrapKey),
            cancellationToken);
        if (!ok)
        {
            throw new InvalidOperationException(
                "ExportPoolToPubkeyAndDownloadAsync: bridge reported failure.");
        }
    }

    /// <summary>
    /// Test/diagnostic seam — assembles the SAME v3 pubkey envelope as
    /// <see cref="ExportPoolToPubkeyAndDownloadAsync"/>, but returns the
    /// composed envelope to managed C# as a <c>byte[]</c> instead of
    /// triggering the anchor-click download.
    /// <para>
    /// This deliberately defeats the streaming memory win (it materialises
    /// the whole envelope on the managed heap), so it is intentionally NOT
    /// on <see cref="IEncryptedSqliteWasmDatabaseService"/>. Its sole reason
    /// to exist is that the production export's download side is unreachable
    /// from in-page test code, so a round-trip test cannot otherwise obtain
    /// a real <c>.eds</c> envelope to feed back into
    /// <see cref="ImportPoolGuidedFromStreamAsync"/>. Caller invariant and
    /// recipient-identity validation are identical to the download variant.
    /// </para>
    /// </summary>
    internal async Task<byte[]> ExportPoolToPubkeyBytesAsync(
        string recipientX25519PublicKeyBase64,
        string recipientCredentialId,
        CancellationToken cancellationToken = default)
    {
        var sessionId = _bridge.NextSessionId();
        try
        {
            var size = await WithPubkeyExportEnvelopeAsync(
                recipientX25519PublicKeyBase64,
                recipientCredentialId,
                (metadataJson, wrapKey) => SqliteWasmWorkerBridge.ExportPoolToBytesSessionAsync(
                    metadataJson, wrapKey, sessionId),
                cancellationToken);

            var envelope = new byte[size];
            var offset = 0;
            while (offset < size)
            {
                var read = SqliteWasmWorkerBridge.ReadExportBytes(
                    sessionId, offset, envelope.AsSpan(offset));
                if (read <= 0)
                {
                    throw new InvalidOperationException(
                        $"ExportPoolToPubkeyBytesAsync: ReadExportBytes returned {read} at " +
                        $"offset {offset} of {size}.");
                }

                offset += read;
            }

            return envelope;
        }
        finally
        {
            SqliteWasmWorkerBridge.DiscardExportBytes(sessionId);
        }
    }

    /// <summary>
    /// Shared v3 pubkey-export assembly: validate the recipient identity,
    /// require Encrypted+Unlocked, generate K_wrap, ECIES-wrap it to the
    /// recipient pubkey, build the metadata JSON, then hand
    /// (metadataJson, K_wrap) to <paramref name="ship"/> — the only step
    /// that differs between the anchor-download path and the bytes-return
    /// test seam. K_wrap is zeroed on every exit (success or throw).
    /// </summary>
    private async Task<TResult> WithPubkeyExportEnvelopeAsync<TResult>(
        string recipientX25519PublicKeyBase64,
        string recipientCredentialId,
        Func<string, ArraySegment<byte>, Task<TResult>> ship,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipientX25519PublicKeyBase64))
        {
            throw new ArgumentException(
                "recipientX25519PublicKeyBase64 must be a non-empty Base64 X25519 pubkey.",
                nameof(recipientX25519PublicKeyBase64));
        }

        if (string.IsNullOrWhiteSpace(recipientCredentialId))
        {
            throw new ArgumentException(
                "recipientCredentialId must be a non-empty Base64 WebAuthn credentialId.",
                nameof(recipientCredentialId));
        }

        // Decode + length-check the recipient pubkey before doing any
        // worker round-trip — mirrors ExportPoolToPubkeyBytesAsync's preflight.
        byte[] recipientPubBytes;
        try
        {
            recipientPubBytes = Convert.FromBase64String(recipientX25519PublicKeyBase64);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(
                "recipientX25519PublicKeyBase64 is not valid Base64.",
                nameof(recipientX25519PublicKeyBase64), ex);
        }

        if (recipientPubBytes.Length != 32)
        {
            throw new ArgumentException(
                $"recipientX25519PublicKeyBase64 must decode to 32 bytes; got {recipientPubBytes.Length}.",
                nameof(recipientX25519PublicKeyBase64));
        }

        var current = await GetStateAsync(cancellationToken);
        if (!current.Encrypted || !current.Unlocked)
        {
            throw new PoolOperationRejectedException(
                PoolOperationRejection.EXPORT_NEEDS_UNLOCK,
                "Pubkey export requires Encrypted + Unlocked — call UnlockAsync first.");
        }

        // K_wrap generation + ECIES wrap. The wrap key is held in C# only
        // long enough to ship to the worker; the worker side wipes its copy
        // after the chunked rekey loop finishes.
        var wrapKeyMem = await _cryptoProvider.GenerateContentKeyAsync();
        var wrapKey = wrapKeyMem.ToArray();
        try
        {
            var wrappedResult = await _cryptoProvider.EncryptAsymmetricFromBytesAsync(
                wrapKey, recipientX25519PublicKeyBase64);
            if (!wrappedResult.Success || wrappedResult.Value is null)
            {
                throw new InvalidOperationException(
                    $"Pubkey export: ECIES wrap of K_wrap failed " +
                    $"({wrappedResult.ErrorCode}).");
            }

            var wrapped = wrappedResult.Value;

            var metadata = new
            {
                version = 3,
                aadVersion = "v1",
                prfSaltBase64 = Convert.ToBase64String(_prfService.HashedSaltBytes),
                ephemeralPublicKey = wrapped.EphemeralPublicKey,
                wrappedContentKeyCiphertext = wrapped.Ciphertext,
                wrappedContentKeyNonce = wrapped.Nonce,
                credentialIdHint = recipientCredentialId,
            };
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);

            return await ship(metadataJson, new ArraySegment<byte>(wrapKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
            if (MemoryMarshal.TryGetArray(wrapKeyMem, out var wrapKeySegment)
                && wrapKeySegment.Array is not null)
            {
                CryptographicOperations.ZeroMemory(wrapKeySegment.AsSpan());
            }
        }
    }

    /// <summary>
    /// Streaming guided-import variant: the envelope arrives as a Stream
    /// (typically <c>IBrowserFile.OpenReadStream</c>) and is shipped to
    /// the worker's import session one ArrayPool chunk at a time. C# managed
    /// heap peak is one chunk (~1 MB); the JS Blob parts list is the
    /// browser's responsibility (Safari disk-backs above ~50 MB).
    ///
    /// Same security contract as <see cref="ImportPoolGuidedFromStreamAsync"/>:
    /// state must be Plain or Encrypted+Locked; envelope's CredentialIdHint
    /// must match <paramref name="credentialId"/>; vfsKey must come from
    /// the WebAuthn ceremony pinned to that credential. Throws otherwise.
    /// Token-equivalent: session id is C#-issued, JS holds it only between
    /// Open and Discard.
    /// </summary>
    public async Task<PoolImportResult> ImportPoolGuidedFromStreamAsync(
        Stream envelopeStream,
        long envelopeSize,
        ReadOnlyMemory<byte> vfsKey,
        string credentialId,
        CancellationToken cancellationToken = default)
    {
        if (envelopeSize <= 0)
        {
            throw new ArgumentException(
                $"envelopeSize must be positive, got {envelopeSize}", nameof(envelopeSize));
        }

        if (string.IsNullOrWhiteSpace(credentialId))
        {
            throw new ArgumentException(
                "credentialId must be a non-empty Base64 WebAuthn credentialId.",
                nameof(credentialId));
        }

        if (vfsKey.Length != 32)
        {
            throw new ArgumentException(
                $"vfsKey must be exactly 32 bytes; got {vfsKey.Length}.", nameof(vfsKey));
        }

        var current = await GetStateAsync(cancellationToken);
        if (current.Encrypted && current.Unlocked)
        {
            throw new PoolOperationRejectedException(
                PoolOperationRejection.GUIDED_IMPORT_NEEDS_LOCK,
                "ImportPoolGuidedFromStreamAsync rejected: disk is Encrypted+Unlocked. " +
                "Lock or Reset first; guided import rebinds the disk to the import's " +
                "credential and is only allowed from Plain or Locked.");
        }

        var sessionId = _bridge.NextSessionId();
        await _bridge.ImportSessionOpenAsync(
            sessionId, "staging", null, envelopeSize, cancellationToken);

        byte[]? wrapKey = null;
        try
        {
            // Push the picked file into the worker one 1 MB chunk at a time.
            // While the first chunk(s) go past, keep a local copy of the
            // first 4 KB — that's the envelope header PeekEnvelopeHeader
            // parses for the credential-id check and the ECIES wrap fields.
            const int chunkSize = 1 << 20;
            using var headerCopy = new MemoryStream(4096);
            var buf = ArrayPool<byte>.Shared.Rent(chunkSize);
            try
            {
                long totalRead = 0;
                while (totalRead < envelopeSize)
                {
                    var read = await envelopeStream.ReadAsync(
                        buf.AsMemory(0, chunkSize), cancellationToken);
                    if (read <= 0)
                    {
                        throw new InvalidOperationException(
                            $"ImportPoolGuidedFromStreamAsync: stream ended at {totalRead} " +
                            $"of {envelopeSize} bytes; envelope is truncated.");
                    }

                    if (headerCopy.Length < 4096)
                    {
                        var keep = (int)Math.Min(read, 4096 - headerCopy.Length);
                        headerCopy.Write(buf, 0, keep);
                    }

                    totalRead += read;
                    await _bridge.ImportSessionAppendAsync(
                        sessionId, buf.AsMemory(0, read), cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf, clearArray: true);
            }

            await _bridge.ImportSessionCloseAsync(sessionId, cancellationToken);

            // Peek the envelope header from the local 4 KB copy — small
            // enough that one MessagePackReader pass on a managed array
            // is cheap, and large enough to cover the positional fields
            // before Files (PrfSalt + 4 strings).
            var header = PeekEnvelopeHeader(headerCopy.ToArray());
            if (header.Version != 3)
            {
                throw new InvalidOperationException(
                    $"ImportPoolGuidedFromStreamAsync: unsupported envelope Version={header.Version} (expected 3).");
            }

            if (string.IsNullOrEmpty(header.CredentialIdHint))
            {
                throw new InvalidOperationException(
                    "ImportPoolGuidedFromStreamAsync: envelope is missing CredentialIdHint.");
            }

            if (!string.Equals(header.CredentialIdHint, credentialId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "ImportPoolGuidedFromStreamAsync: envelope's CredentialIdHint does not " +
                    "match the supplied credentialId.");
            }

            var wrapped = new AsymmetricEncryptedData(
                header.EphemeralPublicKey,
                header.WrappedContentKeyCiphertext,
                header.WrappedContentKeyNonce);
            var unwrapResult = await _prfService.DecryptAsymmetricToBytesAsync(wrapped);
            if (!unwrapResult.Success || unwrapResult.Value is null)
            {
                throw new InvalidOperationException(
                    $"ImportPoolGuidedFromStreamAsync: ECIES unwrap of K_wrap failed " +
                    $"({unwrapResult.ErrorCode}). The envelope may be sealed for a different " +
                    $"recipient pubkey than the one this passkey derives.");
            }

            wrapKey = unwrapResult.Value;
            if (wrapKey.Length != 32)
            {
                throw new InvalidOperationException(
                    $"ImportPoolGuidedFromStreamAsync: unwrapped K_wrap must be 32 bytes; " +
                    $"got {wrapKey.Length}.");
            }

            // Pass 1 — preflight. Worker AEAD-verifies EVERY slot of
            // EVERY file via blob.stream(). Read-only; no pool mutation.
            // Full coverage is the wipe-after-validate invariant: the
            // WipePoolAsync below destroys the existing disk, so nothing
            // the commit pass decrypts may be unverified at that point.
            var preflight = await SqliteWasmWorkerBridge.ImportPoolStreamPreflightFromSessionAsync(
                sessionId, new ArraySegment<byte>(wrapKey));
            if (preflight != (int)PoolImportResult.OK)
            {
                return (PoolImportResult)preflight;
            }

            // Wipe pool + EnterEncrypted under the import's credential.
            await WipePoolAsync(cancellationToken);
            await EnterEncryptedAsync(vfsKey, credentialId, cancellationToken);

            // Pass 2 — commit. Worker rebuilds a fresh Blob from the same
            // session's parts (still live), re-streams via blob.stream(),
            // decrypts under K_wrap and re-encrypts under the freshly-
            // installed globalKey.
            var commitResult = await SqliteWasmWorkerBridge.ImportPoolStreamCommitFromSessionAsync(
                sessionId, new ArraySegment<byte>(wrapKey));
            if (commitResult != (int)PoolImportResult.OK)
            {
                return (PoolImportResult)commitResult;
            }

            // The envelope decides what the pool holds; the app's schema is
            // whatever its model says today. Reconcile through the same host
            // seam the plane-1 imports use, so a headless consumer of the
            // guided import gets it as well.
            var host = _bridge.HostDatabaseService;
            if (host is not null)
            {
                await host.MigrateAsync(cancellationToken);
            }

            ReportDbState(DbInitState.READY);
            return PoolImportResult.OK;
        }
        finally
        {
            if (wrapKey is not null)
            {
                CryptographicOperations.ZeroMemory(wrapKey);
            }

            // Idempotent on every exit — success, AEAD failure, exception.
            // Drops the worker's staged envelope; nothing about it survives
            // the call that needed it.
            await _bridge.ImportSessionDiscardAsync(
                sessionId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Envelope-header peek result — every field the import flow needs
    /// before it can decide to wipe + re-encrypt. <c>Files</c> is
    /// intentionally absent: the streaming worker reads it directly off
    /// the JS-side Blob.
    /// </summary>
    private readonly struct EnvelopeHeader
    {
        public int Version { get; init; }
        public string AadVersion { get; init; }
        public string EphemeralPublicKey { get; init; }
        public string WrappedContentKeyCiphertext { get; init; }
        public string WrappedContentKeyNonce { get; init; }
        public string CredentialIdHint { get; init; }
    }

    /// <summary>
    /// Forward-parse just the envelope's positional header without copying
    /// the <c>Files</c> bulk into managed memory. Uses
    /// <see cref="MessagePackReader"/>'s streaming primitives so the
    /// entries are read in-place from <paramref name="envelope"/>; only
    /// the small string fields are allocated. The 32-byte PrfSalt is
    /// consumed and discarded — the receiver uses local PrfService config;
    /// envelope salt is forward-compat for cross-app import.
    /// </summary>
    private static EnvelopeHeader PeekEnvelopeHeader(byte[] envelope)
    {
        var reader = new MessagePackReader(envelope);
        var arrLen = reader.ReadArrayHeader();
        if (arrLen != 8)
        {
            throw new InvalidOperationException(
                $"PeekEnvelopeHeader: expected envelope array(8), got array({arrLen}).");
        }

        var version = reader.ReadInt32();
        var aadVersion = reader.ReadString()
                         ?? throw new InvalidOperationException("PeekEnvelopeHeader: AadVersion is null.");
        var prfSaltSeq = reader.ReadBytes()
                         ?? throw new InvalidOperationException("PeekEnvelopeHeader: PrfSalt is missing.");
        if (prfSaltSeq.Length != 32)
        {
            throw new InvalidOperationException(
                $"PeekEnvelopeHeader: PrfSalt must be 32 bytes, got {prfSaltSeq.Length}.");
        }

        var ephPub = reader.ReadString()
                     ?? throw new InvalidOperationException("PeekEnvelopeHeader: EphemeralPublicKey is null.");
        var wrapCt = reader.ReadString()
                     ?? throw new InvalidOperationException("PeekEnvelopeHeader: WrappedContentKeyCiphertext is null.");
        var wrapNonce = reader.ReadString()
                        ?? throw new InvalidOperationException("PeekEnvelopeHeader: WrappedContentKeyNonce is null.");
        var credIdHint = reader.ReadString()
                         ?? throw new InvalidOperationException("PeekEnvelopeHeader: CredentialIdHint is null.");
        // Files array tail is intentionally not consumed — the streaming
        // worker reads it directly off the JS-side Blob.
        return new EnvelopeHeader
        {
            Version = version,
            AadVersion = aadVersion,
            EphemeralPublicKey = ephPub,
            WrappedContentKeyCiphertext = wrapCt,
            WrappedContentKeyNonce = wrapNonce,
            CredentialIdHint = credIdHint,
        };
    }

    public ValueTask<string?> ReadEnvelopeCredentialIdHintAsync(
        ReadOnlyMemory<byte> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Forward-parse the positional header via PeekEnvelopeHeader rather
        // than full MessagePackSerializer.Deserialize<EncryptedPoolEnvelope> —
        // works equally well on a small (≥ ~256 byte) prefix as on a full
        // envelope, which is what the streaming import path needs to peek
        // CredentialIdHint without buffering the whole .eds file in C#.
        EnvelopeHeader header;
        try
        {
            header = PeekEnvelopeHeader(envelope.ToArray());
        }
        catch (Exception)
        {
            // Truncated / malformed prefix → no hint available.
            return ValueTask.FromResult<string?>(null);
        }

        if (header.Version != 3 || string.IsNullOrEmpty(header.CredentialIdHint))
        {
            return ValueTask.FromResult<string?>(null);
        }

        return ValueTask.FromResult<string?>(header.CredentialIdHint);
    }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed class PoolManifestBody
{
    [Key(0)]
    public string? CredentialId { get; set; }

    [Key(1)]
    public string? PublicKeyFingerprint { get; set; }
}

/// <summary>
/// Pool-wide disk-manifest state surfaced by
/// <see cref="EncryptedSqliteWasmDatabaseService.ReadManifestAsync"/>.
/// File-internal helper — the only consumer is the encrypted-disk service.
/// </summary>
internal enum ManifestState
{
    /// <summary>No DB carries the manifest magic — disk is Plain.</summary>
    ABSENT,

    /// <summary>Every DB carries an identical, structurally-valid manifest.</summary>
    PRESENT,

    /// <summary>DBs carry different manifest bytes — corruption / partial import.</summary>
    MISMATCH,

    /// <summary>HMAC verification failed (verifyMac=true was passed).</summary>
    TAMPERED,

    /// <summary>Magic present but layout decode failed — corruption.</summary>
    MALFORMED,
}
