using System.Security.Cryptography;
using Microsoft.Extensions.Localization;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.Abstractions.Formatting;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.Crypto.UI.Components.Authentication;
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
        IHostDatabaseService hostDatabaseService,
        StatusModel statusModel,
        IStringLocalizer<EncryptionModel> localizer);

    public partial EncryptedDiskState? State { get; set; }
    public partial string PastedRecipientKey { get; set; } = string.Empty;
    public partial string? PastedRecipientError { get; set; }

    /// <summary>
    /// Mirror of <see cref="IHostDatabaseService.HasAnyDataAsync"/> at
    /// the last <see cref="RefreshAsync"/>. Drives the visibility of the
    /// Plain-disk ZIP-export affordance — the reset service already knows
    /// the host's DbContext factories, so it's the natural place to ask
    /// "any rows?". Defaults to <c>true</c> so the very first paint
    /// doesn't briefly flash the affordance missing before Refresh
    /// completes.
    /// </summary>
    public partial bool HasPlainData { get; set; } = true;

    /// <summary>True when the VFS is plain (no passkey registered yet).</summary>
    public bool IsPlain => State?.Encrypted == false;

    /// <summary>True when the VFS is encrypted and the worker holds the global key.</summary>
    public bool IsUnlocked => State is { Encrypted: true, Unlocked: true };

    /// <summary>True when the VFS is encrypted but the worker has no global key.</summary>
    public bool IsLocked => State is { Encrypted: true, Unlocked: false };

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
    // and recipient share (rekey to recipient K). Plain export off this
    // surface; migrate via LeaveEncrypted → ExportAllDatabases.
    [ObservableCommand(nameof(ExportDiskBackupAsync), nameof(CanExportDisk), nameof(FormatOperationError))]
    public partial IObservableCommandAsync ExportDiskBackup { get; }

    [ObservableCommand(nameof(ExportDiskForRecipientAsync), nameof(CanExportDiskForRecipient), nameof(FormatOperationError))]
    public partial IObservableCommandAsync ExportDiskForRecipient { get; }

    // Replaces the entire pool. Caller (page partial) owns the destructive
    // confirmation dialog; parameter is the picked file's bytes.
    [ObservableCommand(nameof(ImportDiskCmdAsync), nameof(CanImportDisk), nameof(FormatOperationError))]
    public partial IObservableCommandAsync<byte[]> ImportDisk { get; }

    // Plain-disk ZIP batch ops. Each ZIP entry is a standard .db any
    // SQLite tool can open.
    [ObservableCommand(nameof(ExportAllDatabasesAsync), nameof(CanExportAllDatabases), nameof(FormatOperationError))]
    public partial IObservableCommandAsync ExportAllDatabases { get; }

    [ObservableCommand(nameof(ImportAllDatabasesCmdAsync), nameof(CanImportAllDatabases), nameof(FormatOperationError))]
    public partial IObservableCommandAsync<byte[]> ImportAllDatabases { get; }

    // Page-side component-trigger so the model layer stays JSInterop-free.
    [ObservableComponentTriggerAsync]
    public partial PendingDownload? PendingDownload { get; set; }

    private bool CanEnterEncrypted() =>
        IsPlain
        && !string.IsNullOrEmpty(Auth.PublicKey)
        && !string.IsNullOrEmpty(Auth.CredentialId);

    private bool CanLeaveEncrypted() => IsUnlocked;
    private bool CanLock() => IsUnlocked;
    private bool CanSignOut() => IsPlain && !string.IsNullOrEmpty(Auth.PublicKey);
    private bool CanExportDisk() => IsUnlocked;
    private bool CanExportDiskForRecipient() => IsUnlocked && TryGetPastedRecipientIdentity() is not null;

    // Guided import rebinds the disk to the import's credential — only
    // valid from Plain (no current binding) or Locked (binding exists but
    // worker key not installed). Unlocked is rejected: switching credentials
    // mid-session would orphan in-flight EF contexts and is conceptually a
    // Reset+Import, not an Import.
    private bool CanImportDisk() => IsPlain || IsLocked;

    // Plain ZIP export only makes sense on a Plain disk — on an encrypted
    // disk the native .db pages would be unreadable until LeaveEncrypted runs.
    private bool CanExportAllDatabases() => IsPlain;

    // Plain ZIP import is state-aware: Plain → unpack as-is; Locked → break
    // encryption (recovery path); Unlocked → re-encrypt on write under the
    // existing globalKey. Session.ImportAllDatabasesAsync owns the dispatch.
    private bool CanImportAllDatabases() => IsPlain || IsLocked || IsUnlocked;

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        State = await Session.GetStateAsync(cancellationToken);
        HasPlainData = await HostDatabaseService.HasAnyDataAsync(cancellationToken);
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
        Auth.ClearKeys();
        await RefreshAsync(cancellationToken);
        StatusModel.AddWarning(Localizer["Status_Locked"], nameof(Lock));
    }

    private Task SignOutCmdAsync(CancellationToken cancellationToken)
    {
        // Auth.SignOut() flips PublicKey → the OnAuthChangedAsync observer
        // drives Refresh; no manual call needed.
        Auth.SignOut();
        StatusModel.AddWarning(Localizer["Status_SignedOut"], nameof(SignOut));
        return Task.CompletedTask;
    }

    // Single host-side seam owns the full reset sequence (disk wipe + PRF
    // clear + sign-out + per-context re-migrate + boot status → READY).
    // Hosts without recovery register NullHostDatabaseService; the call
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
    private async Task ExportDiskBackupAsync(CancellationToken cancellationToken)
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
        var envelope = await Session.ExportDiskToPubkeyAsync(
            Auth.PublicKey, Auth.CredentialId, cancellationToken);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = $"disk-backup-{stamp}.eds";
        await DownloadBytesAsync(envelope, fileName);
        StatusModel.AddSuccess(
            Localizer["Status_DiskExported", FormatSize(envelope.Length), fileName],
            nameof(ExportDiskBackup));
    }

    private async Task ExportDiskForRecipientAsync(CancellationToken cancellationToken)
    {
        var recipient = TryGetPastedRecipientIdentity()
            ?? throw new InvalidOperationException(
                "Pasted recipient identity is missing or invalid.");
        var envelope = await Session.ExportDiskToPubkeyAsync(
            recipient.PublicKey, recipient.CredentialId, cancellationToken);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = $"disk-recipient-{stamp}.eds";
        await DownloadBytesAsync(envelope, fileName);
        StatusModel.AddSuccess(
            Localizer["Status_DiskExportedForRecipient", FormatSize(envelope.Length), fileName],
            nameof(ExportDiskForRecipient));
    }

    // Guided import — collapses the recipient ritual (Reset → EnterEncrypted
    // → ImportDisk) into one orchestrated call. Reads the envelope's
    // credentialId hint, drives WebAuthn pinned to that passkey, derives the
    // VFS key from the freshly-cached PRF seed, then calls the service's
    // wipe-and-rebind primitive. PRF cache stays populated through the
    // service call so the envelope's ECIES K_wrap can be unwrapped under
    // the same seed.
    private async Task ImportDiskCmdAsync(byte[] envelopeBytes, CancellationToken cancellationToken)
    {
        if (envelopeBytes is null || envelopeBytes.Length == 0)
        {
            throw new InvalidOperationException(
                "Pick a .eds envelope file before importing.");
        }

        var hint = await Session.ReadEnvelopeCredentialIdHintAsync(envelopeBytes, cancellationToken);
        if (string.IsNullOrEmpty(hint))
        {
            throw new InvalidOperationException(
                Localizer["Error_ImportEnvelopeHasNoCredentialId"]);
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
            var result = await Session.ImportDiskGuidedAsync(
                envelopeBytes, vfsKey, hint, cancellationToken);
            if (result == DiskImportResult.WRONG_KEY)
            {
                throw new InvalidOperationException(
                    "Imported envelope's wrap key did not verify under the recipient's PRF-derived priv key " +
                    "(envelope was sealed for a different pubkey than the one the chosen passkey derives).");
            }
            if (result != DiskImportResult.OK)
            {
                throw new InvalidOperationException($"ImportDisk failed: {result}");
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
        StatusModel.AddSuccess(Localizer["Status_DiskImported"], nameof(ImportDisk));
    }

    private async Task ExportAllDatabasesAsync(CancellationToken cancellationToken)
    {
        var zip = await DatabaseService.ExportAllDatabasesAsync(cancellationToken);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = $"databases-{stamp}.zip";
        await DownloadBytesAsync(zip, fileName);
        StatusModel.AddSuccess(
            Localizer["Status_AllExported", FormatSize(zip.Length), fileName],
            nameof(ExportAllDatabases));
    }

    private async Task ImportAllDatabasesCmdAsync(byte[] zipBytes, CancellationToken cancellationToken)
    {
        if (zipBytes is null || zipBytes.Length == 0)
        {
            throw new InvalidOperationException(
                "Pick a .zip archive before importing.");
        }
        // Route via Session (encrypted-side dispatcher) so Plain/Locked/Unlocked
        // each land on the right wipe/encrypt path. The base bridge's
        // ImportAllDatabasesAsync is Plain-disk only and would corrupt an
        // encrypted pool by writing 4096-byte plain pages at slot offsets.
        var result = await Session.ImportAllDatabasesAsync(zipBytes, cancellationToken);
        if (result != DiskImportResult.OK)
        {
            throw new InvalidOperationException($"ImportAllDatabases failed: {result}");
        }
        await RefreshAsync(cancellationToken);
        StatusModel.AddSuccess(Localizer["Status_AllImported"], nameof(ImportAllDatabases));
    }

    private async ValueTask<byte[]> DeriveVfsKeyAsync()
    {
        var derive = await PrfService.DeriveDomainKeyAsync(
            EncryptedDiskLifecycle.VfsDomainId,
            EncryptedDiskLifecycle.VfsHkdfContext);
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

    private async Task DownloadBytesAsync(byte[] bytes, string fileName)
    {
        var tcs = new TaskCompletionSource();
        PendingDownload = new PendingDownload(bytes, fileName, tcs);
        await tcs.Task;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1024.0 / 1024.0:F2} MB",
    };

    private string FormatOperationError(Exception ex) => ex switch
    {
        OperationCanceledException => Localizer["Status_OperationCancelled"],
        InvalidOperationException => Localizer["Error_Operation", ex.Message],
        _ => Localizer["Error_Operation", ex.Message],
    };
}

public sealed record PendingDownload(byte[] Bytes, string FileName, TaskCompletionSource Done);
