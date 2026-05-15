// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using System.IO.Compression;
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
/// alongside <c>IPrfService</c> because <see cref="ResetDiskAsync"/>
/// implicitly clears the PRF cache to keep the auth UI in lockstep with
/// the disk's Plain transition). Tracks <see cref="_isUnlocked"/> in
/// memory to avoid an extra worker round-trip in
/// <see cref="GetStateAsync"/>.
/// </para>
/// </summary>
internal sealed class EncryptedSqliteWasmDatabaseService
    : IEncryptedSqliteWasmDatabaseService, IDatabaseLockProbe
{
    private const int PlainVfsSlotSize = 4096;

    private readonly SqliteWasmWorkerBridge _bridge;
    private readonly EncryptedSqliteWasmWorkerBridge _encryptedBridge;
    private readonly IDbInitializationReporter _reporter;
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
        ICryptoProvider cryptoProvider)
    {
        _bridge = SqliteWasmWorkerBridge.Instance;
        _encryptedBridge = EncryptedSqliteWasmWorkerBridge.Instance;
        _reporter = reporter;
        _status = status;
        _prfService = prfService;
        _cryptoProvider = cryptoProvider;
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
    // EncryptedDiskState down to the three fields plane 1 cares about.
    async Task<DatabaseLockState> IDatabaseLockProbe.GetStateAsync(CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken);
        return new DatabaseLockState(state.Encrypted, state.Unlocked, state.Hint);
    }

    public async Task<EncryptedDiskState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        // Manifest is the source of truth for the Encrypted/Plain axis.
        // Disk-as-unit invariant guarantees one shared manifest across
        // every DB; the worker side asserts equality on read.
        var (state, credentialId) = await ReadManifestAsync(verifyMac: _isUnlocked, cancellationToken);

        switch (state)
        {
            case ManifestState.PRESENT:
                _expectedCredentialId = credentialId;
                _bridge.SetDiskLocked(!_isUnlocked);
                return new EncryptedDiskState(true, _isUnlocked, credentialId);

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
                    _bridge.SetDiskLocked(false);
                    return new EncryptedDiskState(true, true, expectedForHeal);
                }
                _isUnlocked = false;
                _bridge.SetDiskLocked(true);
                return new EncryptedDiskState(true, false, null);

            case ManifestState.MALFORMED:
            case ManifestState.TAMPERED:
                // Surface corruption as Encrypted+Locked with no hint —
                // UI sees the auth panel + reset escape hatch but can't
                // route to a specific passkey because the manifest can't
                // be trusted. Caller's only recovery is ResetDiskAsync.
                _isUnlocked = false;
                _bridge.SetDiskLocked(true);
                return new EncryptedDiskState(true, false, null);

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
    private async Task<EncryptedDiskState> ProbeAbsentManifestAsync(CancellationToken cancellationToken)
    {
        if (_expectedCredentialId is { Length: > 0 } expected && _isUnlocked)
        {
            var dbs = await _bridge.ListDatabasesAsync(cancellationToken);
            if (dbs.Count > 0)
            {
                await WriteManifestAsync(expected, cancellationToken);
            }
            _bridge.SetDiskLocked(false);
            return new EncryptedDiskState(true, true, expected);
        }

        _isUnlocked = false;
        _bridge.SetDiskLocked(false);
        return EncryptedDiskState.Plain;
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
        var (raw, body, _) = await _encryptedBridge.ReadDiskManifestAsync(verifyMac, cancellationToken);
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

        DiskManifestBody decoded;
        try
        {
            decoded = MessagePackSerializer.Deserialize<DiskManifestBody>(body);
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
        var body = new DiskManifestBody
        {
            CredentialId = credentialId,
            PublicKeyFingerprint = string.Empty,
        };
        var bytes = MessagePackSerializer.Serialize(body);
        return _encryptedBridge.WriteDiskManifestAsync(bytes, cancellationToken);
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
        ReportDbState(DbInitState.READY);
    }

    private async Task InstallEncryptionKeyAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        // Disk-as-unit: install globalKey in the worker and release the
        // bridge gate so DB ops route through the encrypted hot path.
        await _encryptedBridge.SetEncryptionKeyAsync(key, cancellationToken);
        _isUnlocked = true;
        _bridge.SetDiskLocked(false);
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
                _bridge.SetDiskLocked(state != ManifestState.ABSENT);
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
        //     in-flight or post-clear DB ops fail with DiskLockedException
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
            _bridge.SetDiskLocked(true);
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
            throw new InvalidOperationException(
                "EnterEncryptedAsync requires EncryptedDiskState.Plain — VFS is already encrypted.");
        }

        var databases = await _bridge.ListDatabasesAsync(cancellationToken);
        var backups = new Dictionary<string, byte[]>(databases.Count, StringComparer.Ordinal);
        try
        {
            // Phase 0: snapshot every plain DB so a later per-DB encrypt or
            // manifest write failure can restore the whole pool to Plain.
            foreach (var db in databases)
            {
                backups[db] = await _bridge.ExportDatabaseAsync(db, cancellationToken);
            }

            // Phase 1: walk every plain DB in OPFS, encrypt-in-place under K.
            // EncryptDatabaseInPlaceAsync is per-DB; we iterate. The worker
            // closes each DB during the conversion, so OFile state can't leak.
            foreach (var db in databases)
            {
                await _encryptedBridge.EncryptDatabaseInPlaceAsync(db, key, cancellationToken);
            }

            // Phase 2: install the global key. EnterEncrypted writes the
            // keyed manifest after install, so verification happens after
            // the write rather than inside the public UnlockAsync path.
            await InstallEncryptionKeyAsync(key, cancellationToken);

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
            await RollBackEnterEncryptedAsync(databases, backups, CancellationToken.None);
            throw;
        }
        finally
        {
            foreach (var backup in backups.Values)
            {
                CryptographicOperations.ZeroMemory(backup);
            }
        }
    }

    private async Task RollBackEnterEncryptedAsync(
        IReadOnlyList<string> databases,
        IReadOnlyDictionary<string, byte[]> backups,
        CancellationToken cancellationToken)
    {
        await _encryptedBridge.ClearEncryptionKeyAsync(cancellationToken);
        _isUnlocked = false;
        _expectedCredentialId = null;
        _bridge.SetDiskLocked(false);

        foreach (var db in databases)
        {
            await _bridge.DeleteDatabaseAsync(db, cancellationToken);
        }

        foreach (var (db, bytes) in backups)
        {
            var result = await _bridge.ImportDatabaseAsync(db, bytes, cancellationToken);
            if (result != DiskImportResult.OK)
            {
                throw new InvalidOperationException(
                    $"EnterEncryptedAsync rollback failed while restoring '{db}' (result={result}).");
            }
        }
    }

    public async Task LeaveEncryptedAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetStateAsync(cancellationToken);
        if (!current.Encrypted || !current.Unlocked)
        {
            throw new InvalidOperationException(
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
        await _encryptedBridge.ClearDiskManifestAsync(cancellationToken);
        await _encryptedBridge.ClearEncryptionKeyAsync(cancellationToken);
        _isUnlocked = false;
        _expectedCredentialId = null;
        _bridge.SetDiskLocked(false);
        ReportDbState(DbInitState.READY);
    }

    public async Task ResetDiskAsync(CancellationToken cancellationToken = default)
    {
        // Scorched-earth boundary: wipe the pool + drop the PRF cache.
        // PrfService.ClearKeys cascades through KeyExpired →
        // AuthenticationModel.OnSessionExpired → PublicKey=null →
        // PrfAuthenticationStateProvider → AuthorizeView re-evaluates.
        // Calling it here means a single Session.ResetDiskAsync() leaves
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
        _bridge.SetDiskLocked(false);
    }

    // ---------------------------------------------------------------------
    // Whole-disk export / import — the disk-as-unit replacement for the
    // pre-fork per-DB Export*. Mixed plain + encrypted DBs is not a
    // representable disk state, so the public surface only exposes
    // whole-pool envelopes; the per-DB worker primitives live on as
    // internal building blocks.
    //
    // Export is asymmetric only: ExportDiskToPubkeyAsync(recipientPub) wraps
    // a fresh K_wrap via ECIES (X25519 ECDH + HKDF + AES-256-GCM) and rekeys
    // the pool under K_wrap. Backup vs share is just "own pubkey vs peer
    // pubkey" at the call site — same code path. The legacy symmetric
    // overload (recipient pastes their VFS key) has been removed; sharing
    // a 32-byte symmetric key was a footgun because anyone with the bytes
    // can decrypt the disk forever.
    //
    // The "encrypted → plain" path used to be a third export shape. It's
    // gone — use LeaveEncryptedAsync to transition to Plain, then the
    // plain interface's ExportAllDatabasesAsync (ZIP of native .db files).
    // Each interface produces its native format; no cross-plane wire
    // confusion.
    // ---------------------------------------------------------------------

    public async Task<byte[]> ExportDiskToPubkeyAsync(
        string recipientX25519PublicKeyBase64,
        string recipientCredentialId,
        CancellationToken cancellationToken = default)
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
        // Decode-and-validate length up front. Recipient pubkey is canonical
        // 32 bytes; anything else is an immediate caller error (UI must
        // unarmor before passing in).
        byte[] recipientPubBytes;
        try
        {
            recipientPubBytes = Convert.FromBase64String(recipientX25519PublicKeyBase64);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(
                "recipientX25519PublicKeyBase64 is not valid Base64.",
                nameof(recipientX25519PublicKeyBase64),
                ex);
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
            throw new InvalidOperationException(
                "ExportDiskToPubkeyAsync requires Encrypted + Unlocked — call UnlockAsync first.");
        }

        // Generate the per-export wrap key (K_wrap). 32 random bytes used
        // both as the symmetric ChaCha20 key for the page-rekey loop AND
        // as the plaintext to ECIES-wrap to the recipient.
        var wrapKeyMem = await _cryptoProvider.GenerateContentKeyAsync();
        var wrapKey = wrapKeyMem.ToArray();
        try
        {
            // ECIES-wrap the wrap key to the recipient's pubkey. The
            // resulting AsymmetricEncryptedData carries the ephemeral
            // pubkey, AES-GCM ciphertext, and nonce — exactly what the
            // recipient needs to invert. Bytes-shaped path (P21) — the
            // 32-byte wrap key never lands in a managed string.
            var wrappedResult = await _cryptoProvider.EncryptAsymmetricFromBytesAsync(
                wrapKey,
                recipientX25519PublicKeyBase64);
            if (!wrappedResult.Success || wrappedResult.Value is null)
            {
                throw new InvalidOperationException(
                    $"ExportDiskToPubkeyAsync: ECIES wrap of K_wrap failed " +
                    $"({wrappedResult.ErrorCode}).");
            }
            var wrapped = wrappedResult.Value;

            // Loop every DB, calling the existing REKEY mode with K_wrap
            // as the target. Same primitive as the legacy symmetric path,
            // just sourcing the target key from ECDH instead of pasted bytes.
            var names = await _bridge.ListDatabasesAsync(cancellationToken);
            var envelope = new EncryptedDiskEnvelope
            {
                Version = 2,
                AadVersion = "v1",
                Files = new List<EncryptedDiskFile>(names.Count),
                EphemeralPublicKey = wrapped.EphemeralPublicKey,
                WrappedContentKeyCiphertext = wrapped.Ciphertext,
                WrappedContentKeyNonce = wrapped.Nonce,
                CredentialIdHint = recipientCredentialId,
            };
            try
            {
                foreach (var name in names)
                {
                    var bytes = await _encryptedBridge.ExportDatabaseAsync(
                        name, VfsExportMode.REKEY, wrapKey, cancellationToken);
                    envelope.Files.Add(new EncryptedDiskFile { Name = name, Bytes = bytes });
                }
                return MessagePackSerializer.Serialize(envelope);
            }
            finally
            {
                envelope.Clear();
            }
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
    /// Import an asymmetric encrypted disk envelope.
    /// </summary>
    public async Task<DiskImportResult> ImportDiskAsync(
        ReadOnlyMemory<byte> envelope,
        CancellationToken cancellationToken = default)
    {
        var current = await GetStateAsync(cancellationToken);

        EncryptedDiskEnvelope decoded;
        try
        {
            decoded = MessagePackSerializer.Deserialize<EncryptedDiskEnvelope>(envelope);
        }
        catch (MessagePackSerializationException ex)
        {
            throw new InvalidOperationException(
                "ImportDiskAsync: envelope is not a valid EncryptedDiskEnvelope (MessagePack decode failed).",
                ex);
        }
        try
        {
            if (decoded.Version != 2)
            {
                throw new InvalidOperationException(
                    $"ImportDiskAsync: unsupported envelope Version={decoded.Version} (expected 2). " +
                    $"Asymmetric envelopes are the only supported format on this branch.");
            }
            return await ImportDiskAsync(decoded, current, cancellationToken);
        }
        finally
        {
            decoded.Clear();
        }
    }

    /// <inheritdoc />
    public async Task<DiskImportResult> ImportAllDatabasesAsync(
        byte[] zipBytes,
        CancellationToken cancellationToken = default)
    {
        if (zipBytes is null || zipBytes.Length == 0)
        {
            throw new ArgumentException(
                "ImportAllDatabasesAsync: zipBytes must be a non-empty ZIP archive.",
                nameof(zipBytes));
        }

        var current = await GetStateAsync(cancellationToken);
        var entries = await ReadPlainSqliteZipEntriesAsync(
            zipBytes,
            requireVfsPageShape: current.Encrypted && current.Unlocked,
            cancellationToken);
        if (entries is null)
        {
            return DiskImportResult.WRONG_KEY;
        }

        // Branch on disk state. Plain delegates straight to the base bridge;
        // Locked breaks encryption (recovery path); Unlocked re-encrypts on
        // write (preserves passkey binding).
        if (!current.Encrypted)
        {
            return await _bridge.ImportAllDatabasesAsync(zipBytes, cancellationToken);
        }

        if (!current.Unlocked)
        {
            // Encrypted+Locked → break encryption. Drop globalKey + delete
            // every DB + clear manifest, then unpack the ZIP via the base
            // bridge (which now writes plain pages because no key is
            // registered). State ends Plain; user can re-encrypt under any
            // new passkey via EnterEncryptedAsync.
            await WipePoolAsync(cancellationToken);
            await _encryptedBridge.ClearDiskManifestAsync(cancellationToken);
            var result = await _bridge.ImportAllDatabasesAsync(zipBytes, cancellationToken);
            if (result == DiskImportResult.OK)
            {
                ReportDbState(DbInitState.READY);
            }
            return result;
        }

        // Encrypted+Unlocked → preserve encryption. Wipe DBs only (keep
        // manifest + globalKey + passkey binding); re-encrypt each ZIP
        // entry on write under the registered globalKey via the worker's
        // importDbPlain handler. State stays Encrypted+Unlocked.
        var existing = await _bridge.ListDatabasesAsync(cancellationToken);
        foreach (var name in existing)
        {
            await _bridge.DeleteDatabaseAsync(name, cancellationToken);
        }

        foreach (var entry in entries)
        {
            var result = await _encryptedBridge.ImportPlainDatabaseAsync(
                entry.Name, entry.Bytes, cancellationToken);
            if (result != DiskImportResult.OK)
            {
                return result;
            }
        }
        return DiskImportResult.OK;
    }

    private static async Task<List<PlainZipEntry>?> ReadPlainSqliteZipEntriesAsync(
        byte[] zipBytes,
        bool requireVfsPageShape,
        CancellationToken cancellationToken)
    {
        var result = new List<PlainZipEntry>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        using var preflightMs = new MemoryStream(zipBytes, writable: false);
        using var preflightZip = new ZipArchive(preflightMs, ZipArchiveMode.Read);
        foreach (var entry in preflightZip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (!IsBareDatabaseName(entry.Name)
                || !string.Equals(entry.FullName, entry.Name, StringComparison.Ordinal)
                || !seenNames.Add(entry.Name)
                || entry.Length > int.MaxValue
                || entry.Length < SqliteWasmWorkerBridge.SqliteHeaderMagic.Length)
            {
                return null;
            }

            using var entryMs = new MemoryStream(checked((int)entry.Length));
            await using (var entryStream = entry.Open())
            {
                await entryStream.CopyToAsync(entryMs, cancellationToken);
            }

            var bytes = entryMs.ToArray();
            if (bytes.Length < SqliteWasmWorkerBridge.SqliteHeaderMagic.Length
                || !bytes.AsSpan(0, SqliteWasmWorkerBridge.SqliteHeaderMagic.Length)
                    .SequenceEqual(SqliteWasmWorkerBridge.SqliteHeaderMagic))
            {
                return null;
            }

            if (requireVfsPageShape
                && (bytes.Length == 0 || bytes.Length % PlainVfsSlotSize != 0))
            {
                return null;
            }

            result.Add(new PlainZipEntry(entry.Name, bytes));
        }

        return result.Count == 0 ? null : result;
    }

    public ValueTask<string?> ReadEnvelopeCredentialIdHintAsync(
        ReadOnlyMemory<byte> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EncryptedDiskEnvelope decoded;
        try
        {
            decoded = MessagePackSerializer.Deserialize<EncryptedDiskEnvelope>(envelope);
        }
        catch (MessagePackSerializationException)
        {
            return ValueTask.FromResult<string?>(null);
        }
        try
        {
            if (decoded.Version != 2 || string.IsNullOrEmpty(decoded.CredentialIdHint))
            {
                return ValueTask.FromResult<string?>(null);
            }
            return ValueTask.FromResult<string?>(decoded.CredentialIdHint);
        }
        finally
        {
            decoded.Clear();
        }
    }

    public async Task<DiskImportResult> ImportDiskGuidedAsync(
        ReadOnlyMemory<byte> envelope,
        ReadOnlyMemory<byte> vfsKey,
        string credentialId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentialId))
        {
            throw new ArgumentException(
                "credentialId must be a non-empty Base64 WebAuthn credentialId.",
                nameof(credentialId));
        }
        if (vfsKey.Length != 32)
        {
            throw new ArgumentException(
                $"vfsKey must be exactly 32 bytes; got {vfsKey.Length}.",
                nameof(vfsKey));
        }

        var current = await GetStateAsync(cancellationToken);
        if (current.Encrypted && current.Unlocked)
        {
            throw new InvalidOperationException(
                "ImportDiskGuidedAsync rejected: disk is Encrypted+Unlocked. " +
                "Lock or Reset first; the guided import rebinds the disk to the " +
                "import's credential and is only allowed from Plain or Locked.");
        }

        EncryptedDiskEnvelope decoded;
        try
        {
            decoded = MessagePackSerializer.Deserialize<EncryptedDiskEnvelope>(envelope);
        }
        catch (MessagePackSerializationException ex)
        {
            throw new InvalidOperationException(
                "ImportDiskGuidedAsync: envelope is not a valid EncryptedDiskEnvelope (MessagePack decode failed).",
                ex);
        }

        try
        {
            if (decoded.Version != 2)
            {
                throw new InvalidOperationException(
                    $"ImportDiskGuidedAsync: unsupported envelope Version={decoded.Version} (expected 2).");
            }
            if (string.IsNullOrEmpty(decoded.CredentialIdHint))
            {
                throw new InvalidOperationException(
                    "ImportDiskGuidedAsync: envelope is missing CredentialIdHint — " +
                    "cannot verify it matches the supplied credentialId.");
            }
            if (!string.Equals(decoded.CredentialIdHint, credentialId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "ImportDiskGuidedAsync: envelope's CredentialIdHint does not match " +
                    "the supplied credentialId. The caller must derive vfsKey from the " +
                    "passkey identified by the envelope's hint.");
            }

            var prepare = await TryUnwrapAndPreflightImportAsync(decoded, cancellationToken);
            if (prepare.Result != DiskImportResult.OK)
            {
                return prepare.Result;
            }
            var wrapKey = prepare.WrapKey
                ?? throw new InvalidOperationException(
                    "ImportDiskGuidedAsync: preflight succeeded without a wrap key.");

            try
            {
                // Phase 1: wipe pool (drop globalKey + delete files) WITHOUT
                // clearing the PRF cache — the ECIES K_wrap unwrap has already
                // succeeded and the same cached seed remains live through the
                // import.
                await WipePoolAsync(cancellationToken);

                // Phase 2: re-enter encrypted mode under (vfsKey, credentialId).
                // Pool is empty post-wipe; EnterEncryptedAsync's per-DB encrypt
                // loop is a no-op and the manifest is stamped onto whatever DBs
                // the import creates (handled internally via _expectedCredentialId).
                await EnterEncryptedAsync(vfsKey, credentialId, cancellationToken);

                // Phase 3: rekey-import every envelope file using the already
                // unwrapped and preflighted K_wrap. The freshly-installed
                // globalKey is what the worker re-encrypts pages under after
                // decrypting K_wrap. State is Encrypted+Unlocked (just set by
                // EnterEncrypted), so the inner import invariant holds.
                var postEnter = await GetStateAsync(cancellationToken);
                var importResult = await ImportDiskAsync(
                    decoded, postEnter, wrapKey, cancellationToken);

                // Flip DbState to READY so AuthorizeView Policy="DatabaseOpen"
                // re-evaluates as authorized for downstream pages. The Locked
                // entry path leaves DbState at ENCRYPTED_LOCKED through wipe +
                // EnterEncrypted (neither reports); without this explicit flip
                // the encryption page shows Unlocked but TodoList/Notes etc.
                // still see ENCRYPTED_LOCKED and refuse to render their DB
                // surface. Idempotent on the Plain entry path (READY → READY
                // is dropped by Report's same-state guard).
                if (importResult == DiskImportResult.OK)
                {
                    ReportDbState(DbInitState.READY);
                }
                return importResult;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapKey);
            }
        }
        finally
        {
            decoded.Clear();
        }
    }

    /// <summary>
    /// Recipient side of the asymmetric (v2) disk-import flow.
    /// <list type="number">
    ///   <item>ECIES-unwrap the envelope's <see cref="EncryptedDiskEnvelope.WrappedContentKeyCiphertext"/>
    ///         through the caller's PRF-derived cached keyId to recover the
    ///         per-export 32-byte wrap key (<c>K_wrap</c>).</item>
    ///   <item>For each file, hand <c>K_wrap</c> + page ciphertext to the
    ///         worker bridge's <see cref="SqliteWasmWorkerBridge.ImportDatabaseWithRekeyAsync"/>;
    ///         the worker decrypts under <c>K_wrap</c> and re-encrypts under
    ///         the registered globalKey before writing.</item>
    /// </list>
    /// Caller invariant: must be Encrypted+Unlocked. The disk's globalKey
    /// stays bound to the caller's PRF-VFS-key — <c>K_wrap</c> is
    /// transit-only and never persists.
    /// </summary>
    private async Task<DiskImportResult> ImportDiskAsync(
        EncryptedDiskEnvelope decoded,
        EncryptedDiskState current,
        CancellationToken cancellationToken)
    {
        if (!current.Encrypted || !current.Unlocked)
        {
            throw new InvalidOperationException(
                "ImportDiskAsync (v2): asymmetric envelope import requires the recipient " +
                "disk to be Encrypted+Unlocked under their own VFS key. Sign in and Encrypt " +
                "the VFS first.");
        }

        var prepare = await TryUnwrapAndPreflightImportAsync(decoded, cancellationToken);
        if (prepare.Result != DiskImportResult.OK)
        {
            return prepare.Result;
        }
        var wrapKey = prepare.WrapKey
            ?? throw new InvalidOperationException(
                "ImportDiskAsync (v2): preflight succeeded without a wrap key.");

        try
        {
            return await ImportDiskAsync(decoded, current, wrapKey, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    private async Task<DiskImportResult> ImportDiskAsync(
        EncryptedDiskEnvelope decoded,
        EncryptedDiskState current,
        byte[] wrapKey,
        CancellationToken cancellationToken)
    {
        if (!current.Encrypted || !current.Unlocked)
        {
            throw new InvalidOperationException(
                "ImportDiskAsync (v2): asymmetric envelope import requires the recipient " +
                "disk to be Encrypted+Unlocked under their own VFS key. Sign in and Encrypt " +
                "the VFS first.");
        }

        // All files were verified by TryUnwrapAndPreflightImportAsync before
        // this destructive branch runs. Caller still owns the user-facing
        // confirmation dialog.
        var existing = await _bridge.ListDatabasesAsync(cancellationToken);
        foreach (var name in existing)
        {
            await _bridge.DeleteDatabaseAsync(name, cancellationToken);
        }

        foreach (var file in decoded.Files)
        {
            var result = await _encryptedBridge.ImportDatabaseWithRekeyAsync(
                file.Name, wrapKey, file.Bytes, cancellationToken);
            if (result != DiskImportResult.OK)
            {
                // Worker rejected post-preflight — non-recoverable local IO
                // failure, not a key/AEAD issue (preflight already proved
                // the envelope bytes under K_wrap).
                return result;
            }
        }

        if (current.Hint is { Length: > 0 } credentialId)
        {
            _expectedCredentialId = credentialId;
            await WriteManifestAsync(credentialId, cancellationToken);
            await VerifyUnlockedManifestAsync(allowAbsentForEmptyPool: false, cancellationToken);
        }
        return DiskImportResult.OK;
    }

    private async Task<(DiskImportResult Result, byte[]? WrapKey)> TryUnwrapAndPreflightImportAsync(
        EncryptedDiskEnvelope decoded,
        CancellationToken cancellationToken)
    {
        ValidateImportEnvelopeShape(decoded);

        var wrapped = new AsymmetricEncryptedData(
            decoded.EphemeralPublicKey,
            decoded.WrappedContentKeyCiphertext,
            decoded.WrappedContentKeyNonce);
        var unwrapResult = await _prfService.DecryptAsymmetricToBytesAsync(wrapped);
        if (!unwrapResult.Success || unwrapResult.Value is null)
        {
            throw new InvalidOperationException(
                $"ImportDiskAsync (v2): ECIES unwrap of K_wrap failed " +
                $"({unwrapResult.ErrorCode}). The envelope may be sealed for a different " +
                $"recipient pubkey than the one this passkey derives.");
        }

        // K_wrap arrives as raw bytes — never crosses System.String (P21).
        var wrapKey = unwrapResult.Value;
        if (wrapKey.Length != 32)
        {
            CryptographicOperations.ZeroMemory(wrapKey);
            throw new InvalidOperationException(
                $"ImportDiskAsync (v2): unwrapped K_wrap must be 32 bytes; got {wrapKey.Length}.");
        }

        try
        {
            // AEAD preflight — runs the worker's non-destructive
            // verifyImportRekey for every envelope file under K_wrap.
            // No OPFS write, no key install. This method is deliberately
            // called before any wipe in both normal and guided import flows.
            foreach (var file in decoded.Files)
            {
                var preflight = await _encryptedBridge.VerifyImportRekeyAsync(
                    file.Name, wrapKey, file.Bytes, cancellationToken);
                if (preflight != DiskImportResult.OK)
                {
                    CryptographicOperations.ZeroMemory(wrapKey);
                    return (preflight, null);
                }
            }

            return (DiskImportResult.OK, wrapKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(wrapKey);
            throw;
        }
    }

    private static void ValidateImportEnvelopeShape(EncryptedDiskEnvelope decoded)
    {
        if (decoded.EphemeralPublicKey.Length == 0
            || decoded.WrappedContentKeyCiphertext.Length == 0
            || decoded.WrappedContentKeyNonce.Length == 0)
        {
            throw new InvalidOperationException(
                "ImportDiskAsync (v2): envelope is missing the ECIES-wrap fields " +
                "(EphemeralPublicKey / WrappedContentKeyCiphertext / WrappedContentKeyNonce).");
        }
        if (decoded.Files.Count == 0)
        {
            throw new InvalidOperationException(
                "ImportDiskAsync (v2): envelope contains no database files.");
        }

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in decoded.Files)
        {
            if (!IsBareDatabaseName(file.Name))
            {
                throw new InvalidOperationException(
                    $"ImportDiskAsync (v2): envelope database name '{file.Name}' must be a bare file name.");
            }
            if (!seenNames.Add(file.Name))
            {
                throw new InvalidOperationException(
                    $"ImportDiskAsync (v2): envelope contains duplicate database name '{file.Name}'.");
            }
        }
    }

    private static bool IsBareDatabaseName(string name)
        => !string.IsNullOrWhiteSpace(name)
            && name != "."
            && name != ".."
            && name.IndexOf('/') < 0
            && name.IndexOf('\\') < 0
            && string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal);

}

internal sealed record PlainZipEntry(string Name, byte[] Bytes);

[MessagePackObject(AllowPrivate = true)]
internal sealed class DiskManifestBody
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
