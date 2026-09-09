using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;
using ObservableCollections;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.Abstractions.Formatting;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.Crypto.UI.Components.Authentication;
using SqliteWasmBlazor.Crypto.UI.Abstractions;
using SqliteWasmBlazor.Crypto.UI.Services;

namespace SqliteWasmBlazor.Crypto.UI.Components.Encryption;

// Commands + state for the encryption page. Lifecycle hooks + page-topology
// documentation live in the .Lifecycle.cs partial sibling.
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class EncryptionModel : ObservableModel
{
    public partial EncryptionModel(
        IEncryptedSqliteWasmDatabaseService session,
        ISqliteWasmDatabaseService databaseService,
        AuthenticationModel auth,
        DbStateModel dbState,
        IPrfService prfService,
        ISecureKeyCache keyCache,
        IHostRecoveryService hostDatabaseService,
        StatusModel statusModel,
        IStringLocalizer<EncryptionModel> localizer);

    public partial EncryptedPoolState? State { get; set; }
    public partial string PastedRecipientKey { get; set; } = string.Empty;
    public partial string? PastedRecipientError { get; set; }

    /// <summary>
    /// Every database row the panel shows: the union of what the SAH pool
    /// holds (<c>ISqliteWasmDatabaseService.ListDatabasesAsync</c>) and what
    /// the host declares it owns, refreshed by <see cref="RefreshAsync"/>.
    /// Each row carries its own export checkbox, import picker and
    /// clear/remove button — the per-row shape is what keeps "replace this
    /// one database" apart from "replace everything".
    /// </summary>
    public partial IReadOnlyList<PoolDatabaseEntry> Databases { get; set; } = [];

    /// <summary>
    /// Currently-selected DB names for the plain export affordance. UI
    /// binds a list of checkboxes (one per present <see cref="Databases"/>
    /// entry) to <see cref="ObservableList{T}.Add"/> /
    /// <see cref="ObservableList{T}.Remove"/> on this list. Cardinality
    /// decides the export shape: one entry → vanilla <c>.db</c> file;
    /// ≥ 2 entries → <c>.dbs</c> MessagePack envelope (no compression).
    /// </summary>
    public partial ObservableList<string> SelectedDatabases { get; private init; } = new();

    /// <summary>True when the VFS is plain (no passkey registered yet).</summary>
    public bool IsPlain => State?.Encrypted == false;

    /// <summary>True when the VFS is encrypted and the worker holds the global key.</summary>
    public bool IsUnlocked => State is { Encrypted: true, Unlocked: true };

    /// <summary>True when the VFS is encrypted but the worker has no global key.</summary>
    public bool IsLocked => State is { Encrypted: true, Unlocked: false };

    /// <summary>
    /// Button label for <see cref="Reset"/>. A plain pool only loses its
    /// databases; an encrypted one loses the passkey binding with them, and
    /// the button has to say so before it is clicked. A state that could not
    /// be read yet falls to the encrypted wording — the heavier warning is
    /// the safe default when the pool's state is unknown.
    /// </summary>
    public string ResetLabel => IsPlain ? Localizer["Btn_Reset"] : Localizer["Btn_ResetEncrypted"];

    /// <summary>Hint copy paired with <see cref="ResetLabel"/>.</summary>
    public string ResetHint => IsPlain ? Localizer["Hint_ResetPlain"] : Localizer["Hint_ResetEncrypted"];

    /// <summary>Confirmation-dialog body paired with <see cref="ResetLabel"/>.</summary>
    public string ResetConfirmation => IsPlain ? Localizer["Confirm_Reset"] : Localizer["Confirm_ResetEncrypted"];

    [ObservableCommand(nameof(RefreshAsync))]
    public partial IObservableCommandAsync Refresh { get; }

    [ObservableCommand(nameof(EnterEncryptedCmdAsync), nameof(CanEnterEncrypted), nameof(FormatOperationError))]
    public partial IObservableCommandAsync EnterEncrypted { get; }

    [ObservableCommand(nameof(LeaveEncryptedCmdAsync), nameof(CanLeaveEncrypted), nameof(FormatOperationError))]
    public partial IObservableCommandAsync LeaveEncrypted { get; }

    [ObservableCommand(nameof(LockCmdAsync), nameof(CanLock), nameof(FormatOperationError))]
    public partial IObservableCommandAsync Lock { get; }

    [ObservableCommand(nameof(ResetCmdAsync), null, nameof(FormatOperationError))]
    public partial IObservableCommandAsync Reset { get; }

    // Plain-disk sign-out — disabled on Encrypted (disk is bound to one
    // credential; switching keys requires Reset or recipient export/import).
    [ObservableCommand(nameof(SignOutCmdAsync), nameof(CanSignOut), nameof(FormatOperationError))]
    public partial IObservableCommandAsync SignOut { get; }

    // Encrypted-disk envelope ops. Encrypted+Unlocked only. Two flavours:
    // backup (verbatim ciphertext under current K, no re-encryption cost)
    // and recipient share (rekey to recipient K).
    [ObservableCommand(nameof(ExportPoolBackupAsync), nameof(CanExportPool), nameof(FormatOperationError))]
    public partial IObservableCommandAsync ExportPoolBackup { get; }

    [ObservableCommand(nameof(ExportPoolForRecipientAsync), nameof(CanExportPoolForRecipient), nameof(FormatOperationError))]
    public partial IObservableCommandAsync ExportPoolForRecipient { get; }

    // Replaces the entire pool and rebinds it to the envelope's passkey.
    // Runs from every disk state — an unlocked pool is locked by the command
    // itself, because "Lock first, then import" is a step the user should
    // not have to discover from an error. Caller (page partial) owns the
    // destructive confirmation dialog; parameter is the picked file itself —
    // the model streams it into the worker one ArrayPool chunk at a time so
    // no heap holds more than a chunk regardless of envelope size.
    [ObservableCommand(nameof(ImportPoolCmdAsync), null, nameof(FormatOperationError))]
    public partial IObservableCommandAsync<IBrowserFile> ImportPool { get; }

    // Per-row plain export: one database as a vanilla .db file any SQLite
    // tool opens. Plain disk emits verbatim; Encrypted+Unlocked decrypts
    // slot-by-slot before emit; Encrypted+Locked is refused.
    [ObservableCommand(nameof(ExportDatabaseCmdAsync), nameof(CanExportDatabases), nameof(FormatOperationError))]
    public partial IObservableCommandAsync<string> ExportDatabase { get; }

    // Bundle export: the ticked databases as one streaming .dbs envelope
    // (MessagePack array of [name, bytes], no compression). Only meaningful
    // for two or more — a single database is the row's own export button,
    // which produces a file SQLite can open directly.
    [ObservableCommand(nameof(ExportDatabasesCmdAsync), nameof(CanExportBundle), nameof(FormatOperationError))]
    public partial IObservableCommandAsync ExportDatabases { get; }

    // Single-DB plain import: streaming write into the database the picked
    // row names (see SingleDatabaseImport for why the name is not the
    // file's). Plain writes plain pages; Encrypted+Unlocked rekey-on-writes
    // under globalKey; Encrypted+Locked is refused (the .eds guided import
    // is the rebind-to-new-credential path).
    [ObservableCommand(nameof(ImportDatabaseCmdAsync), nameof(CanImportDatabases), nameof(FormatOperationError))]
    public partial IObservableCommandAsync<SingleDatabaseImport> ImportDatabase { get; }

    // Multi-DB bundle import: streaming <c>.dbs</c> envelope that replaces
    // the whole pool. Same disk-state gating as the single-DB path.
    [ObservableCommand(nameof(ImportDatabasesCmdAsync), nameof(CanImportDatabases), nameof(FormatOperationError))]
    public partial IObservableCommandAsync<IBrowserFile> ImportDatabases { get; }

    // Pool housekeeping: empty one database. An owned database is re-created
    // empty in the same command (the app would otherwise query a hole); an
    // unowned one is simply gone. The pool outlives the app's own databases
    // — imports and retired features leave entries behind — so the row list
    // is also where strays get cleaned up.
    [ObservableCommand(nameof(DeleteDatabaseCmdAsync), nameof(CanDeleteDatabase), nameof(FormatOperationError))]
    public partial IObservableCommandAsync<string> DeleteDatabase { get; }

    private bool CanEnterEncrypted() =>
        IsPlain
        && !string.IsNullOrEmpty(Auth.PublicKey)
        && !string.IsNullOrEmpty(Auth.CredentialId);

    private bool CanLeaveEncrypted() => IsUnlocked;
    private bool CanLock() => IsUnlocked;
    private bool CanSignOut() => IsPlain && !string.IsNullOrEmpty(Auth.PublicKey);
    private bool CanExportPool() => IsUnlocked;
    private bool CanExportPoolForRecipient() => IsUnlocked && TryGetPastedRecipientIdentity() is not null;

    // Plain export needs a disk state that can produce plain pages. Plain
    // pools emit verbatim; Encrypted+Unlocked decrypts on read. Locked has
    // no key to decrypt with — refuse.
    private bool CanExportDatabases() => IsPlain || IsUnlocked;

    // The bundle is the multi-database shape; below two ticks the row's own
    // export is the better file, so the command stays disabled.
    private bool CanExportBundle() =>
        CanExportDatabases() && SelectedDatabases.Count >= 2;

    // Plain import (single .db or multi-DB .dbs envelope) needs a writable
    // state. Plain writes plain pages; Encrypted+Unlocked rekey-on-writes.
    // Locked is refused — the .eds guided import is the rebind-to-new-
    // credential path; this affordance assumes the disk's key is installed.
    private bool CanImportDatabases() => IsPlain || IsUnlocked;

    // Pool deletion needs a writable pool: Plain writes plain pages,
    // Encrypted+Unlocked holds the key the SAHPool needs to keep the
    // remaining slots consistent. Locked is refused like every other
    // pool-mutating command.
    private bool CanDeleteDatabase() => IsPlain || IsUnlocked;

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        // Every route into this panel's state passes here, so this is the only
        // place the guard belongs. A panel can render before anything has
        // started the worker — Blazor runs OnAfterRenderAsync
        // child-before-parent, so a page beats the layout's
        // <SqliteWasmDatabaseInitializer/> — and asking a worker that does not
        // exist yet comes back "SQLite not initialized". Nothing is lost by
        // returning: OnDbStateChangedAsync brings us back the moment the state
        // says there is something to talk to.
        if (!DbState.State.IsWorkerAvailable())
        {
            return;
        }

        State = await Session.GetStateAsync(cancellationToken);
        // Rebuild the row list on every state transition so a newly-created
        // or freshly-imported database shows up immediately. Rows are the
        // union of pool content and host-owned names: an owned database the
        // pool has lost still gets a row (so a backup can be imported into
        // it), and a pool entry the app doesn't open still gets one (so it
        // can be exported or removed).
        // Parks belong to an import that is either in flight or died with
        // its tab; they are pool bookkeeping, not databases anyone opens.
        var present = (await DatabaseService.ListDatabasesAsync(cancellationToken))
            .Where(name => !PoolNaming.IsImportPark(name))
            .ToArray();
        var presentSet = new HashSet<string>(present, StringComparer.Ordinal);
        var owned = HostDatabaseService.OwnedDatabases;
        var ownedSet = new HashSet<string>(owned, StringComparer.Ordinal);
        Databases =
        [
            .. present
                .Concat(owned.Where(name => !presentSet.Contains(name)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => new PoolDatabaseEntry(
                    name, ownedSet.Contains(name), presentSet.Contains(name))),
        ];
        // Drop any selections that point at DBs no longer in the pool so the
        // export button disables itself once the last surviving choice is
        // gone. ObservableList has no RemoveWhere, so collect doomed names
        // first to avoid mutating during iteration.
        var doomed = SelectedDatabases.Where(name => !presentSet.Contains(name)).ToArray();
        foreach (var name in doomed)
        {
            SelectedDatabases.Remove(name);
        }
    }

    private async Task EnterEncryptedCmdAsync(CancellationToken cancellationToken)
    {
        var credentialId = Auth.CredentialId;
        if (string.IsNullOrEmpty(credentialId))
        {
            throw new InvalidOperationException(
                "Authenticate or register a passkey before encrypting the VFS.");
        }

        var keyBytes = await DeriveVfsKeyAsync();
        try
        {
            await Session.EnterEncryptedAsync(keyBytes, credentialId, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }

        await RefreshAsync(cancellationToken);
        StatusModel.AddSuccess(Localizer["Status_Encrypted"], nameof(EnterEncrypted));
    }

    private async Task LeaveEncryptedCmdAsync(CancellationToken cancellationToken)
    {
        await Session.LeaveEncryptedAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
        StatusModel.AddWarning(Localizer["Status_Decrypted"], nameof(LeaveEncrypted));
    }

    private async Task LockCmdAsync(CancellationToken cancellationToken)
    {
        await Session.LockAsync(cancellationToken);
        await Auth.ClearKeysAsync();
        await RefreshAsync(cancellationToken);
        StatusModel.AddWarning(Localizer["Status_Locked"], nameof(Lock));
    }

    private async Task SignOutCmdAsync(CancellationToken cancellationToken)
    {
        // SignOutAsync flips PublicKey → the OnAuthChangedAsync observer
        // drives Refresh; no manual call needed.
        await Auth.SignOutAsync();
        StatusModel.AddWarning(Localizer["Status_SignedOut"], nameof(SignOut));
    }

    // Single host-side seam owns the full reset sequence (disk wipe + PRF
    // clear + sign-out + per-context re-migrate + boot status → READY).
    // Hosts without recovery register NullHostRecoveryService; the call
    // no-ops there.
    private async Task ResetCmdAsync(CancellationToken cancellationToken)
    {
        await HostDatabaseService.ResetAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
        StatusModel.AddWarning(Localizer["Status_Reset"], nameof(Reset));
    }

    // Backup-to-self uses the asymmetric path with the caller's OWN pubkey
    // as the recipient — restore from the same passkey re-derives the
    // matching wrap key. The credentialId stamped into the envelope is the
    // caller's own, so the guided import drives WebAuthn back to the same
    // passkey on restore.
    private async Task ExportPoolBackupAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(Auth.PublicKey))
        {
            throw new InvalidOperationException(
                "Cannot back up: not signed in (no X25519 public key available).");
        }
        if (string.IsNullOrEmpty(Auth.CredentialId))
        {
            throw new InvalidOperationException(
                "Cannot back up: not signed in (no WebAuthn credentialId available).");
        }
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = $"disk-backup-{stamp}.eds";
        await Session.ExportPoolToPubkeyAndDownloadAsync(
            fileName, Auth.PublicKey, Auth.CredentialId, cancellationToken);
        StatusModel.AddSuccess(
            Localizer["Status_PoolExported", fileName],
            nameof(ExportPoolBackup));
    }

    private async Task ExportPoolForRecipientAsync(CancellationToken cancellationToken)
    {
        var recipient = TryGetPastedRecipientIdentity()
            ?? throw new InvalidOperationException(
                "Pasted recipient identity is missing or invalid.");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = $"disk-recipient-{stamp}.eds";
        await Session.ExportPoolToPubkeyAndDownloadAsync(
            fileName, recipient.PublicKey, recipient.CredentialId, cancellationToken);
        StatusModel.AddSuccess(
            Localizer["Status_PoolExportedForRecipient", fileName],
            nameof(ExportPoolForRecipient));
    }

    /// <summary>
    /// Per-row plain export — one database as <c>{stem}-{stamp}.db</c>, the
    /// format <c>sqlite3 file.db</c> opens directly. Plain pools emit
    /// verbatim; Encrypted+Unlocked decrypts each slot on read.
    /// </summary>
    private async Task ExportDatabaseCmdAsync(
        string databaseName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException(
                "databaseName must be non-empty.");
        }
        var stem = databaseName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            ? databaseName[..^3]
            : databaseName;
        var fileName = $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss}.db";
        await DatabaseService.ExportDatabaseToDownloadAsync(databaseName, fileName, cancellationToken);
        StatusModel.AddSuccess(
            Localizer["Status_SingleDbExported", databaseName, fileName],
            nameof(ExportDatabase));
    }

    /// <summary>
    /// Bundle export — the ticked databases as one streaming <c>.dbs</c>
    /// envelope (<c>databases-{stamp}.dbs</c>). Plain pools emit verbatim;
    /// Encrypted+Unlocked decrypts each file slot-by-slot so the entries are
    /// vanilla SQLite. Two databases minimum: one is the row's own export,
    /// which produces a file that needs no unpacking
    /// (<see cref="CanExportBundle"/> gates the button).
    /// </summary>
    private async Task ExportDatabasesCmdAsync(CancellationToken cancellationToken)
    {
        var picked = SelectedDatabases.ToArray();
        if (picked.Length < 2)
        {
            throw new InvalidOperationException(
                "Tick at least two databases before exporting a bundle.");
        }
        var fileName = $"databases-{DateTime.Now:yyyyMMdd-HHmmss}.dbs";
        await DatabaseService.ExportDatabasesToDownloadAsync(picked, fileName, cancellationToken);
        StatusModel.AddSuccess(
            Localizer["Status_DbsExported", picked.Length, fileName],
            nameof(ExportDatabases));
    }

    /// <summary>
    /// Guided import — collapses the recipient ritual (Lock → Reset →
    /// EnterEncrypted → ImportPool) into one orchestrated call. The picked
    /// file's stream is pushed into the worker one ArrayPool chunk at a
    /// time, staged there, and re-streamed for AEAD preflight + per-slot
    /// rekey commit. Heap peak stays at one chunk (~1 MB) on both sides.
    ///
    /// Flow: peek envelope header (first ~4 KB only) → read CredentialIdHint
    /// → end the current session if one is open → drive WebAuthn pinned to
    /// the envelope's passkey → derive VFS key from the freshly-cached PRF
    /// seed → call Session.ImportPoolGuidedFromStreamAsync → re-migrate the
    /// host's databases. The PRF cache stays populated from the ceremony
    /// through the service call so the envelope's ECIES K_wrap can be
    /// unwrapped under the same seed.
    /// </summary>
    private async Task ImportPoolCmdAsync(IBrowserFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Size == 0)
        {
            throw new InvalidOperationException(
                "Pick a .eds envelope file before importing.");
        }

        // Header peek — read just the first 4 KB of the picked file so we
        // can extract the CredentialIdHint before driving WebAuthn. The
        // envelope's positional MessagePack header (version + AadVer +
        // PrfSalt + 4 strings) fits comfortably in 4 KB.
        const int peekBytes = 4096;
        var headerSize = (int)Math.Min(peekBytes, file.Size);
        var headerBuffer = new byte[headerSize];
        await using (var peekStream = file.OpenReadStream(maxAllowedSize: file.Size, cancellationToken))
        {
            var read = 0;
            while (read < headerSize)
            {
                var n = await peekStream.ReadAsync(
                    headerBuffer.AsMemory(read, headerSize - read), cancellationToken);
                if (n <= 0) { break; }
                read += n;
            }
            if (read < headerSize)
            {
                Array.Resize(ref headerBuffer, read);
            }
        }

        var hint = await Session.ReadEnvelopeCredentialIdHintAsync(headerBuffer, cancellationToken);
        if (string.IsNullOrEmpty(hint))
        {
            throw new InvalidOperationException(
                Localizer["Error_ImportEnvelopeHasNoCredentialId"]);
        }

        // The import rebinds the pool to the envelope's credential, so the
        // current session has to end first — the service refuses outright on
        // Encrypted+Unlocked. Lock rather than Reset: the existing data
        // stays on disk until the envelope has passed AEAD preflight, so a
        // bad file leaves the pool exactly as it was. The page's confirm
        // dialog has already told the user this signs them out.
        if (IsUnlocked)
        {
            await Session.LockAsync(cancellationToken);
            await Auth.ClearKeysAsync();
            await RefreshAsync(cancellationToken);
        }

        // WebAuthn pinned to the envelope's credentialId — bypasses
        // AuthenticationModel.ApplySessionAsync because that guard rejects
        // any credential whose id doesn't match the current disk hint
        // (would reject this credential up front for the Locked case).
        var derive = await PrfService.DeriveKeysAsync(hint);
        if (derive.Cancelled)
        {
            throw new OperationCanceledException(
                "User cancelled the passkey ceremony.",
                cancellationToken);
        }
        if (!derive.Success || derive.Value is null)
        {
            throw new InvalidOperationException(
                $"PRF derive for envelope's credentialId failed: " +
                $"{derive.Error ?? derive.ErrorCode?.ToString() ?? "unknown"}");
        }
        var importedPublicKey = derive.Value;

        var vfsKey = await DeriveVfsKeyAsync();
        try
        {
            // Re-open the file's stream for the chunked import — the peek
            // stream above was consumed for 4 KB; IBrowserFile yields a
            // fresh stream per OpenReadStream call. The service streams
            // the full body into the worker's import session one ArrayPool
            // chunk at a time, then drives preflight + commit.
            await using var importStream = file.OpenReadStream(
                maxAllowedSize: file.Size, cancellationToken);
            var result = await Session.ImportPoolGuidedFromStreamAsync(
                importStream, file.Size, vfsKey, hint, cancellationToken);
            if (result == PoolImportResult.WRONG_KEY)
            {
                throw new InvalidOperationException(
                    "Imported envelope's wrap key did not verify under the recipient's PRF-derived priv key " +
                    "(envelope was sealed for a different pubkey than the one the chosen passkey derives).");
            }
            if (result != PoolImportResult.OK)
            {
                throw new InvalidOperationException($"ImportPool failed: {result}");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vfsKey);
        }

        // Disk's manifest is now bound to (hint, importedPublicKey). Rebind
        // Auth state without the disk-mismatch guard — the disk has just
        // been rewritten to match this credential.
        Auth.ApplyImportedSession(hint, importedPublicKey);

        await RefreshAsync(cancellationToken);
        StatusModel.AddSuccess(Localizer["Status_PoolImported"], nameof(ImportPool));
    }

    /// <summary>
    /// Single-DB plain-import command. Streams the picked <c>.db</c> file
    /// into <see cref="SingleDatabaseImport.DatabaseName"/>, replacing that
    /// database wholesale. The chunks go straight into the worker, so no
    /// heap holds more than one of them regardless of file size. Plain pools write plain pages;
    /// Encrypted+Unlocked rekey-on-writes under <c>globalKey</c>;
    /// Encrypted+Locked is refused (the <c>.eds</c> guided import is the
    /// rebind-to-new-credential path; <see cref="CanImportDatabases"/> gates
    /// the button).
    ///
    /// <para>
    /// The target must be a database this panel already lists — the user
    /// picked the file on its row. A free-hand name would create a pool
    /// entry no connection string points at, which is storage nothing can
    /// ever read back.
    /// </para>
    /// <para>
    /// The import is staged: the file lands under a temporary pool name and
    /// the host checks it against the target's model
    /// (<see cref="IHostDatabaseService.ValidateSchemaAsync"/>) before
    /// anything is replaced. A TodoDb file picked on the NotesDb row is
    /// rejected with NotesDb untouched — the file names say nothing about
    /// what is inside, so the tables have to.
    /// </para>
    /// </summary>
    private async Task ImportDatabaseCmdAsync(
        SingleDatabaseImport request, CancellationToken cancellationToken)
    {
        var file = request.File;
        if (file is null || file.Size == 0)
        {
            throw new InvalidOperationException(
                "Pick a .db file before importing.");
        }
        if (!file.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported single-database file '{file.Name}': expected .db.");
        }
        var target = request.DatabaseName;
        if (!Databases.Any(entry => string.Equals(entry.Name, target, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"'{target}' is not one of this app's databases.");
        }
        await using var stream = file.OpenReadStream(
            maxAllowedSize: file.Size, cancellationToken);
        await DatabaseService.ImportDatabaseFromStreamAsync(
            target,
            stream,
            file.Size,
            (imported, ct) => ValidateImportedAsync(imported, file.Name, ct),
            cancellationToken);
        await RefreshAsync(cancellationToken);
        StatusModel.AddSuccess(
            Localizer["Status_SingleDbImported", target],
            nameof(ImportDatabase));
    }

    /// <summary>
    /// Host schema gate for a staged import, restated in the user's
    /// language. The host's check knows which tables are missing but not
    /// which file the user picked, and its own message is English written
    /// for a log — so only the table names travel, and the sentence around
    /// them comes from the resx. Anything that isn't a schema mismatch
    /// keeps its own diagnostic.
    /// </summary>
    private async ValueTask ValidateImportedAsync(
        string databaseName, string source, CancellationToken cancellationToken)
    {
        try
        {
            await HostDatabaseService.ValidateSchemaAsync(
                databaseName, databaseName, cancellationToken);
        }
        catch (SchemaMismatchException ex)
        {
            throw new InvalidOperationException(
                Localizer[
                    "Error_SchemaMismatch",
                    source,
                    databaseName,
                    string.Join(", ", ex.MissingTables)],
                ex);
        }
    }

    /// <summary>
    /// Multi-DB plain-import command. Replays every <c>[name, bytes]</c>
    /// entry of a <c>.dbs</c> envelope through the chunked write path,
    /// wiping the existing pool first — the pool ends up being exactly what
    /// the envelope carries. Streaming and disk-state rules match
    /// <see cref="ImportDatabaseCmdAsync"/>.
    /// </summary>
    private async Task ImportDatabasesCmdAsync(IBrowserFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Size == 0)
        {
            throw new InvalidOperationException(
                "Pick a .dbs bundle before importing.");
        }
        if (!file.Name.EndsWith(".dbs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported bundle file '{file.Name}': expected .dbs.");
        }
        await using var stream = file.OpenReadStream(
            maxAllowedSize: file.Size, cancellationToken);
        await DatabaseService.ImportDatabasesFromStreamAsync(
            stream,
            file.Size,
            (imported, ct) => ValidateImportedAsync(imported, file.Name, ct),
            cancellationToken);
        await RefreshAsync(cancellationToken);
        StatusModel.AddSuccess(
            Localizer["Status_DbsImported", file.Name],
            nameof(ImportDatabases));
    }

    /// <summary>
    /// Empty one database and refresh the rows. Two outcomes, and the row
    /// decides which: an owned database is deleted and immediately
    /// re-created empty, because the app opens it by connection string and
    /// a missing file would come back as a schema-less one on the next
    /// query. An unowned entry is storage nothing reads — it is simply
    /// gone.
    /// </summary>
    private async Task DeleteDatabaseCmdAsync(
        string databaseName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException(
                "databaseName must be non-empty.");
        }
        var owned = Databases.Any(entry =>
            entry.Owned && string.Equals(entry.Name, databaseName, StringComparison.Ordinal));
        await DatabaseService.DeleteDatabaseAsync(databaseName, cancellationToken);
        if (owned)
        {
            await HostDatabaseService.MigrateAsync(cancellationToken);
        }
        await RefreshAsync(cancellationToken);
        StatusModel.AddSuccess(
            Localizer[owned ? "Status_DatabaseCleared" : "Status_DatabaseDeleted", databaseName],
            nameof(DeleteDatabase));
    }

    private async ValueTask<byte[]> DeriveVfsKeyAsync()
    {
        var derive = await PrfService.DeriveDomainKeyAsync(
            EncryptedPoolLifecycle.VfsDomainId,
            EncryptedPoolLifecycle.VfsHkdfContext);
        if (!derive.Success || derive.Value is null)
        {
            throw new InvalidOperationException(
                $"VFS key derive failed: {derive.Error ?? derive.ErrorCode?.ToString() ?? "unknown"}");
        }
        var bytes = KeyCache.TryGet(derive.Value)
            ?? throw new InvalidOperationException(
                "VFS key not present in secure cache after derive.");
        if (bytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidOperationException(
                $"VFS key must be 32 bytes, got {bytes.Length}.");
        }
        return bytes;
    }

    // Parse pasted PFA-armored recipient identity — both the X25519 PUBLIC
    // key (32 bytes) and the WebAuthn credentialId are required. The
    // credentialId is the metadata field embedded in the armored payload
    // by the recipient's PublicKeyDisplay (G1). Raw-Base64 pastes without
    // metadata are rejected because the guided-import flow (G3) needs the
    // credentialId to drive WebAuthn's allowCredentials.
    // Sets PastedRecipientError so the markup can show the inline diagnostic.
    private (string PublicKey, string CredentialId)? TryGetPastedRecipientIdentity()
    {
        if (string.IsNullOrWhiteSpace(PastedRecipientKey))
        {
            PastedRecipientError = null;
            return null;
        }

        var (base64Key, metadata) = PrfArmor.UnArmorPublicKeyWithMetadata(PastedRecipientKey);
        if (base64Key is null)
        {
            PastedRecipientError = Localizer["Error_PastedKey_BadBase64"];
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Key);
        }
        catch (FormatException)
        {
            PastedRecipientError = Localizer["Error_PastedKey_BadBase64"];
            return null;
        }

        if (bytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(bytes);
            PastedRecipientError = Localizer["Error_PastedKey_WrongLength", bytes.Length];
            return null;
        }

        if (string.IsNullOrEmpty(metadata?.CredentialId))
        {
            CryptographicOperations.ZeroMemory(bytes);
            PastedRecipientError = Localizer["Error_PastedKey_NoCredentialId"];
            return null;
        }

        CryptographicOperations.ZeroMemory(bytes);
        PastedRecipientError = null;
        return (base64Key, metadata.CredentialId);
    }

    // A disk-state refusal is a fact about the pool, not a defect: say what
    // the pool needs in the user's language. Everything else keeps the
    // diagnostic text — an unexpected failure the user may have to report.
    private string FormatOperationError(Exception ex) => ex switch
    {
        OperationCanceledException => Localizer["Status_OperationCancelled"],
        PoolOperationRejectedException rejected => Localizer[
            rejected.Reason switch
            {
                PoolOperationRejection.ENTER_NEEDS_PLAIN => "Error_Rejected_EnterNeedsPlain",
                PoolOperationRejection.LEAVE_NEEDS_UNLOCK => "Error_Rejected_LeaveNeedsUnlock",
                PoolOperationRejection.EXPORT_NEEDS_UNLOCK => "Error_Rejected_ExportNeedsUnlock",
                PoolOperationRejection.PLAIN_IMPORT_NEEDS_UNLOCK => "Error_Rejected_ImportNeedsUnlock",
                PoolOperationRejection.GUIDED_IMPORT_NEEDS_LOCK => "Error_Rejected_GuidedImportNeedsLock",
                _ => throw new InvalidOperationException(
                    $"Unhandled pool rejection '{rejected.Reason}'."),
            }],
        _ => Localizer["Error_Operation", ex.Message],
    };
}
