// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Snapshot of the encrypted-disk state, reported by
/// <see cref="IEncryptedSqliteWasmDatabaseService.GetStateAsync"/>.
/// </summary>
/// <param name="Encrypted">
/// True when the on-disk passkey manifest is present on every DB in the
/// SAH pool — the disk contains ciphertext databases under the manifest's
/// credentialId. False = Plain (no manifest written, no legacy hint).
/// </param>
/// <param name="Unlocked">
/// True when the worker has the global key installed and reads/writes
/// can proceed. False with <see cref="Encrypted"/> = true means
/// <i>Locked</i> (next op fails until <see cref="IEncryptedSqliteWasmDatabaseService.UnlockAsync"/>).
/// </param>
/// <param name="Hint">
    /// The encrypted-disk passkey credentialId from the on-disk manifest.
    /// Set whenever <see cref="Encrypted"/> = true. UI uses
/// it to render "Sign in with passkey: {hint}" so the user recognises
/// which credential to provide. Null when the manifest is in a corrupted
/// state (Mismatch / Tampered / Malformed) — UI surfaces a "ResetDisk
/// required" affordance instead of routing to a specific credential.
/// </param>
public sealed record EncryptedDiskState(bool Encrypted, bool Unlocked, string? Hint)
{
    /// <summary>
    /// Pristine disk — no registered passkey, no global key.
    /// </summary>
    public static EncryptedDiskState Plain { get; } = new(false, false, null);
}

/// <summary>
/// Encrypted-disk lifecycle + whole-disk envelope I/O. The encrypted disk
/// is a <i>virtual encrypted device</i>: one global key, every database in
/// the SAH pool encrypted under it, one-shot unlock at session start, lock
/// at session end. Whole-disk only — per-DB sharing isn't supported (mixed
/// plain + encrypted state is not representable).
///
/// <para>
/// <b>Audience.</b> Apps wanting passkey-encrypted persistence wrap their
/// content in <c>&lt;AuthorizeView Policy="DatabaseOpen"&gt;</c> and call
/// the lifecycle methods (Enter/Leave/Lock/Unlock/Reset) plus
/// Export/ImportDisk for backup/restore/share. Plain SQLite-on-OPFS apps
/// don't need this interface at all — they use
/// <see cref="ISqliteWasmDatabaseService"/> only.
/// </para>
///
/// <para>
/// <b>Wire format.</b> Export/Import always uses the opaque MessagePack
/// <see cref="EncryptedDiskEnvelope"/> bundle (multi-DB inside one blob,
/// per-file ciphertext under either the current globalKey or a recipient
/// key). For native SQLite interop, the canonical path is
/// <see cref="LeaveEncryptedAsync"/> → plain interface
/// <c>ExportAllDatabasesAsync()</c> (ZIP of native .db).
/// </para>
///
/// <para>
/// <b>State source of truth.</b> The on-disk passkey manifest
/// (bytes 524..1023 of every SAH slot's plaintext header sector — see
/// <c>worker/vfs-prf/manifest.ts</c>) is the encrypted/plain signal:
/// manifest present ⇒ encrypted, manifest absent ⇒ plain. The manifest
/// is owned by this interface (Enter writes it atomically as the last
/// step; Leave/Reset clear it). Co-located with the SAH slots so it
/// cannot drift from the disk's actual content.
/// </para>
///
/// <para>
/// Registered as a singleton by <c>AddSqliteWasm()</c>. Production code
/// consumes this interface; bridge primitives (per-DB encrypt-in-place,
/// mode-based export) are internal implementation detail.
/// </para>
/// </summary>
public interface IEncryptedSqliteWasmDatabaseService
{
    /// <summary>
    /// Current state — derived from the on-disk manifest (presence of the
    /// PFAM magic on every DB ⇒ Encrypted) plus the in-memory tracking of
    /// whether <see cref="UnlockAsync"/> has run since the last
    /// <see cref="LockAsync"/>. One bridge round-trip per call (manifest
    /// region of every DB is read and compared); cheap.
    /// </summary>
    Task<EncryptedDiskState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Unlock the disk with the supplied 32-byte ChaCha20-Poly1305 key.
    /// The credential hint is owned by the auth flow; this method only
    /// ships the key to the worker. The worker closes every cached DB at
    /// the session boundary so SQLite's page cache can't leak K_old
    /// plaintext into a K_new session. Idempotent.
    /// </summary>
    /// <param name="key">Exactly 32 bytes of key material.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UnlockAsync(ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lock the disk — drops the worker's <c>globalKey</c> and force-
    /// closes every open DB. The passkey hint is preserved so the next
    /// boot still sees the disk as encrypted (and prompts for credentials).
    /// Idempotent — when called on a Plain disk just clears any stray key
    /// without engaging the disk-locked gate.
    /// </summary>
    Task LockAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Plain → Encrypted transition.</b> Encrypts every database
    /// currently in the SAH pool in place under <paramref name="key"/>,
    /// installs the global key, then writes <paramref name="credentialId"/>
    /// into the on-disk manifest of every DB so <see cref="GetStateAsync"/>
    /// reflects Encrypted+Unlocked. Manifest write is the last step —
    /// atomic from the consumer's perspective: either the whole transition
    /// succeeds or the disk stays Plain.
    ///
    /// <para>
    /// Caller invariant: <see cref="GetStateAsync"/> must currently
    /// return <see cref="EncryptedDiskState.Plain"/>. Throws
    /// <see cref="InvalidOperationException"/> otherwise. The auth flow's
    /// Register ceremony returns the credential id to pass here.
    /// </para>
    /// </summary>
    /// <param name="key">Exactly 32 bytes of key material.</param>
    /// <param name="credentialId">
    /// The WebAuthn credential id returned by the Register ceremony.
    /// Stored in the manifest body and consulted by the auth flow for
    /// the wrong-passkey early-out.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EnterEncryptedAsync(ReadOnlyMemory<byte> key,
        string credentialId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Encrypted → Plain transition.</b> Decrypts every database in the
    /// SAH pool in place under the active <c>globalKey</c>, clears the
    /// passkey hint, and drops the global key. The disk ends in
    /// <see cref="EncryptedDiskState.Plain"/>. After this, native SQLite
    /// interop is available via <see cref="ISqliteWasmDatabaseService"/>'s
    /// per-DB or batch-ZIP export.
    ///
    /// <para>
    /// Caller invariant: must currently be Encrypted+Unlocked. Throws
    /// <see cref="InvalidOperationException"/> otherwise. Caller is
    /// separately responsible for revoking the passkey credential at the
    /// WebAuthn layer.
    /// </para>
    /// </summary>
    Task LeaveEncryptedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asymmetric disk export. The recipient is identified by their X25519
    /// PUBLIC key (Base64) + their WebAuthn credentialId — never their VFS
    /// key. The sender:
    /// <list type="number">
    ///   <item>Generates a random 32-byte content key (the per-export disk
    ///         wrap key, <c>K_wrap</c>).</item>
    ///   <item>Wraps <c>K_wrap</c> via ECIES (X25519 ECDH +
    ///         HKDF + AES-256-GCM) to <paramref name="recipientX25519PublicKeyBase64"/>.</item>
    ///   <item>Rekeys every page from the current <c>globalKey</c> under
    ///         <c>K_wrap</c> (existing slot-rekey primitive).</item>
    ///   <item>Emits a v2 <see cref="EncryptedDiskEnvelope"/> with the
    ///         page ciphertext + the wrapped key +
    ///         <paramref name="recipientCredentialId"/> stamped into
    ///         <see cref="EncryptedDiskEnvelope.CredentialIdHint"/>.</item>
    /// </list>
    /// Recipient unwraps with their PRF-derived X25519 private key; only the
    /// holder of that key can recover <c>K_wrap</c>. The credentialId hint
    /// lets the guided-import UI drive WebAuthn's <c>allowCredentials</c>
    /// to the exact passkey the recipient must authenticate with.
    /// <para>
    /// <b>Backup vs share.</b> "Backup to self" passes the caller's own
    /// X25519 pubkey + credentialId — the caller's passkey re-derives the
    /// matching private key on next session. "Share with peer" passes the
    /// peer's pubkey + credentialId (both carried in the peer's armored PFA
    /// PUBLIC KEY block). Mechanics identical; only the recipient identity
    /// differs.
    /// </para>
    /// <para>
    /// Caller invariant: must be Encrypted+Unlocked. Throws otherwise.
    /// <paramref name="recipientX25519PublicKeyBase64"/> must decode to a
    /// 32-byte raw X25519 pubkey (UI is responsible for unarmoring before
    /// calling). <paramref name="recipientCredentialId"/> must be a
    /// non-empty Base64 credentialId.
    /// </para>
    /// </summary>
    Task<byte[]> ExportDiskToPubkeyAsync(
        string recipientX25519PublicKeyBase64,
        string recipientCredentialId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace the entire SAH pool with the contents of
    /// <paramref name="envelope"/>. Preflights every file against the current
    /// disk state/key before wiping any currently-registered DB. Auto-detects
    /// per-file content (plain SQLite pages vs slot-format ciphertext) via
    /// the SQLite magic-header probe in
    /// <see cref="ISqliteWasmDatabaseService.ImportDatabaseAsync"/>.
    ///
    /// <para>
    /// <b>Caller is responsible for explicit user confirmation in UI</b> —
    /// this method does not prompt and the wipe step is destructive and
    /// non-recoverable after preflight succeeds. Per the disk-as-unit model,
    /// partial imports are not supported: either the envelope preflight fails
    /// without changing the disk, or the envelope replaces the disk.
    /// </para>
    /// <para>
    /// Caller invariant: disk must NOT be Encrypted+Locked (Plain or
    /// Encrypted+Unlocked are both accepted). Per-file content kind in
    /// the envelope must match the current disk state (importing
    /// ciphertext on a Plain disk yields <see cref="DiskImportResult.WRONG_KEY"/>;
    /// importing plain on an Encrypted+Unlocked disk yields the same).
    /// </para>
    /// </summary>
    /// <returns>
    /// <see cref="DiskImportResult.OK"/> on success;
    /// <see cref="DiskImportResult.WRONG_KEY"/> if content kind does not
    /// match the current disk state or any per-file AEAD preflight rejects
    /// the active globalKey.
    /// </returns>
    Task<DiskImportResult> ImportDiskAsync(
        ReadOnlyMemory<byte> envelope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// State-aware plain-ZIP disk import. The ZIP is the same format produced
    /// by <see cref="ISqliteWasmDatabaseService.ExportAllDatabasesAsync"/> —
    /// one entry per DB, each entry a plain SQLite file. The destination disk
    /// state determines what survives the wipe:
    /// <list type="bullet">
    ///   <item><b>Plain</b> → delegates to
    ///     <see cref="ISqliteWasmDatabaseService.ImportAllDatabasesAsync"/>;
    ///     state stays Plain.</item>
    ///   <item><b>Encrypted+Locked</b> → drops <c>globalKey</c>, clears the
    ///     manifest, deletes every DB, then unpacks the ZIP. The act of
    ///     importing plain bytes onto a Locked disk is the recovery path
    ///     when the passkey is unreachable; state ends Plain. Caller can
    ///     <see cref="EnterEncryptedAsync"/> under any new passkey afterwards.</item>
    ///   <item><b>Encrypted+Unlocked</b> → keeps <c>globalKey</c> + manifest +
    ///     passkey binding, deletes every DB, then re-encrypts each ZIP entry
    ///     under the existing <c>globalKey</c> on write. State stays
    ///     Encrypted+Unlocked.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Caller is responsible for explicit user confirmation in UI</b> —
    /// the wipe is destructive and non-recoverable after preflight. Preflight
    /// validates that every ZIP entry is a plain SQLite file (first 16 bytes
    /// match <c>"SQLite format 3\0"</c>); a mismatched entry returns
    /// <see cref="DiskImportResult.WRONG_KEY"/> without touching the disk.
    /// </para>
    /// </summary>
    /// <returns>
    /// <see cref="DiskImportResult.OK"/> on success;
    /// <see cref="DiskImportResult.WRONG_KEY"/> if any ZIP entry fails the
    /// SQLite-magic preflight (wipe is skipped in this case).
    /// </returns>
    Task<DiskImportResult> ImportAllDatabasesAsync(
        byte[] zipBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Peek inside a disk-import envelope to extract its
    /// <see cref="EncryptedDiskEnvelope.CredentialIdHint"/>. Used by the
    /// guided-import UI to know which passkey to drive WebAuthn against
    /// BEFORE the wipe-and-rebind ritual starts. Pure deserialize — does
    /// not touch the pool or PRF state.
    /// </summary>
    /// <returns>
    /// The envelope's credentialId hint, or <c>null</c> if the envelope is
    /// malformed, an unsupported version, or carries an empty hint.
    /// </returns>
    ValueTask<string?> ReadEnvelopeCredentialIdHintAsync(
        ReadOnlyMemory<byte> envelope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Guided-import primitive that collapses the recipient ritual
    /// (Reset → EnterEncrypted → ImportDisk) into one atomic call. The
    /// caller has already run the WebAuthn ceremony pinned to the
    /// envelope's <see cref="EncryptedDiskEnvelope.CredentialIdHint"/> and
    /// derived <paramref name="vfsKey"/> from the cached PRF seed; the PRF
    /// cache must remain populated through the call so the envelope's
    /// ECIES <c>K_wrap</c> can be unwrapped under the same seed.
    /// <para>
    /// Sequence: ECIES-unwrap <c>K_wrap</c> + AEAD-preflight every envelope
    /// file before touching the current pool → wipe the existing pool
    /// (without clearing the PRF cache) → <see cref="EnterEncryptedAsync"/>
    /// under (<paramref name="vfsKey"/>, <paramref name="credentialId"/>) →
    /// rekey-import every envelope file under the freshly-installed
    /// <c>globalKey</c>. The disk ends Encrypted+Unlocked, bound to the
    /// import's credential, with the envelope's contents written under the
    /// new <c>globalKey</c>.
    /// </para>
    /// <para>
    /// Caller invariant: state must be <see cref="EncryptedDiskState.Plain"/>
    /// or Encrypted+Locked. Encrypted+Unlocked is rejected — caller must
    /// <see cref="LockAsync"/> first; refusing here keeps the state
    /// machine free of covert in-place key swaps. The envelope's
    /// <c>CredentialIdHint</c> must match <paramref name="credentialId"/>.
    /// </para>
    /// </summary>
    /// <returns>
    /// <see cref="DiskImportResult.OK"/> on success;
    /// <see cref="DiskImportResult.WRONG_KEY"/> if AEAD preflight rejects
    /// the envelope's wrap key (envelope was not sealed to the caller's
    /// pubkey).
    /// </returns>
    Task<DiskImportResult> ImportDiskGuidedAsync(
        ReadOnlyMemory<byte> envelope,
        ReadOnlyMemory<byte> vfsKey,
        string credentialId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scorched-earth disk reset. Closes every open DB, drops
    /// <c>globalKey</c>, deletes every DB file from the pool, and clears
    /// the PRF cache so the auth UI flips to NotAuthorized in lockstep.
    /// The next state probe sees an empty pool (manifest absent) and
    /// reports <see cref="EncryptedDiskState.Plain"/>.
    ///
    /// <para>
    /// Wiping the files is part of the contract — without it, the next
    /// <see cref="EnterEncryptedAsync"/> would walk the still-present
    /// ciphertext slots, run encrypt-in-place against them, and the
    /// worker's plain-source shape check would reject. So Reset →
    /// re-Encrypt → Import is the canonical recipient flow for the
    /// asymmetric envelope, and Reset is the only "factory reset"
    /// affordance the UI needs.
    /// </para>
    /// <para>
    /// Use for "factory reset" UX or test fixture cleanup. For routine
    /// session lifecycle, prefer <see cref="LockAsync"/> /
    /// <see cref="LeaveEncryptedAsync"/> — those don't destroy data.
    /// </para>
    /// </summary>
    Task ResetDiskAsync(CancellationToken cancellationToken = default);
}
