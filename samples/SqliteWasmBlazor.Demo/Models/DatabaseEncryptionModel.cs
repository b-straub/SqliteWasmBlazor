using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.Abstractions.Formatting;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Services;
using SqliteWasmBlazor.Crypto.UI.Components.Authentication;
using SqliteWasmBlazor.Models;
using PublicKeyMetadata = SqliteWasmBlazor.Crypto.Abstractions.Formatting.PublicKeyMetadata;

namespace SqliteWasmBlazor.Demo.Models;

/// <summary>
/// Drives the <c>DatabaseEncryption.razor</c> page — the WebAuthn-PRF demo
/// surface for the base-plane VFS encryption primitives shipped through
/// pre-3.a (in-place encrypt / decrypt + <c>VfsExportMode.ENCRYPT</c>).
///
/// <para>
/// The page is gated by <c>&lt;AuthorizeView&gt;</c> driven by
/// <see cref="PrfAuthenticationStateProvider"/>: the NotAuthorized branch
/// shows <c>&lt;AuthenticationPanel/&gt;</c> + <c>&lt;RegistrationPanel/&gt;</c>
/// from Crypto.UI; the Authorized branch shows the DB-state pill + the
/// encrypt / decrypt / export / lock / wipe commands this model owns. The
/// cross-assembly composition is RXBG061-safe (the panels live in
/// <c>SqliteWasmBlazor.Crypto.UI</c>).
/// </para>
///
/// <para>
/// <b>Auth-state reactivity.</b> <see cref="AuthenticationModel"/> is
/// injected so <see cref="OnContextReadyAsync(System.Threading.CancellationToken)"/>
/// can subscribe to <see cref="AuthenticationModel.PublicKey"/> changes and
/// re-run <see cref="RefreshAsync(System.Threading.CancellationToken)"/>;
/// without that, an encrypted DB's <see cref="ItemCount"/> stays null after
/// the user authenticates because the initial pre-auth refresh ran without
/// a registered key. The <c>&lt;AuthorizeView&gt;</c> handles the visual
/// transition; the model handles the data refresh.
/// </para>
///
/// <para>
/// <b>Encryption-state detection.</b> There is no C#-side
/// <c>IsPathEncryptedAsync</c> bridge yet — instead the model probes the
/// 16-byte SQLite magic header (<c>"SQLite format 3\0"</c>) on a
/// <see cref="VfsExportMode.VERBATIM"/> read. Plain DBs start with the
/// magic, encrypted DBs have ChaCha20 ciphertext in slot 0 (~2^-128 odds
/// of accidental match — same threshold the worker uses on the plain-source
/// guard in <c>encryptDatabaseInPlace</c>).
/// </para>
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class DatabaseEncryptionModel : ObservableModel
{
    public partial DatabaseEncryptionModel(
        ISqliteWasmDatabaseService databaseService,
        IDbContextFactory<TodoDbContext> contextFactory,
        IPrfService prfService,
        IOptions<PrfOptions> prfOptions,
        AuthenticationModel auth,
        StatusModel statusModel,
        IStringLocalizer<DatabaseEncryptionModel> localizer);

    /// <summary>The database this page targets — the same DB TodoList writes to.</summary>
    public const string DatabaseName = "TodoDb.db";

    /// <summary>Domain label used by <see cref="PublicKeyMetadata"/> on the armored own-pubkey block.</summary>
    private const string DomainId = "sqlite-vfs:" + DatabaseName;

    private static readonly byte[] SqliteMagicHeader =
        [0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x33, 0x00];

    public partial bool Exists { get; set; }
    public partial bool? IsEncrypted { get; set; }
    public partial int? ItemCount { get; set; }
    public partial long SizeBytes { get; set; }
    public partial string? OwnArmoredPubkey { get; set; }
    public partial string PastedArmoredPubkey { get; set; } = string.Empty;
    public partial string? PastedKeyError { get; set; }

    /// <summary>
    /// True when the active passkey has produced a cached X25519 pubkey.
    /// Read by the <c>CanEncrypt</c> / <c>CanDecrypt</c> /
    /// <c>CanExportForRecipient</c> guards. Cross-model reactivity to
    /// <see cref="AuthenticationModel.PublicKey"/> is handled by the
    /// <see cref="OnContextReadyAsync(System.Threading.CancellationToken)"/>
    /// subscription that re-runs <see cref="RefreshAsync(System.Threading.CancellationToken)"/>
    /// on every auth-state flip; the page itself is gated by
    /// <c>&lt;AuthorizeView&gt;</c> via <c>PrfAuthenticationStateProvider</c>,
    /// so these guards are belt-and-braces inside the Authorized branch.
    /// </summary>
    public bool HasCachedKey => !string.IsNullOrEmpty(Auth.PublicKey);

    [ObservableCommand(nameof(RefreshAsync))]
    public partial IObservableCommandAsync Refresh { get; }

    [ObservableCommand(nameof(EncryptInPlaceAsync), nameof(CanEncrypt), nameof(FormatStateError))]
    public partial IObservableCommandAsync EncryptInPlace { get; }

    [ObservableCommand(nameof(DecryptInPlaceAsync), nameof(CanDecrypt), nameof(FormatStateError))]
    public partial IObservableCommandAsync DecryptInPlace { get; }

    [ObservableCommand(nameof(ExportAsync), nameof(CanExport), nameof(FormatStateError))]
    public partial IObservableCommandAsync Export { get; }

    [ObservableCommand(nameof(ExportForRecipientAsync), nameof(CanExportForRecipient), nameof(FormatStateError))]
    public partial IObservableCommandAsync ExportForRecipient { get; }

    [ObservableCommand(nameof(LockAsync))]
    public partial IObservableCommandAsync Lock { get; }

    [ObservableCommand(nameof(WipeAsync), nameof(CanWipe), nameof(FormatStateError))]
    public partial IObservableCommandAsync Wipe { get; }

    private bool CanEncrypt() => Exists && IsEncrypted == false && HasCachedKey;
    private bool CanDecrypt() => Exists && IsEncrypted == true && HasCachedKey;
    private bool CanExport() => Exists;
    private bool CanExportForRecipient() => Exists && HasCachedKey && TryGetPastedKeyBytes() is not null;
    private bool CanWipe() => Exists;

    /// <summary>
    /// Auto-detected internal observer (RxBlazorV2): private async method
    /// that reads <see cref="AuthenticationModel.PublicKey"/>; the SG wires
    /// a subscription so this runs every time auth state flips. Without it,
    /// an encrypted DB's <see cref="ItemCount"/> stays null after auth
    /// because the initial pre-auth <see cref="RefreshAsync"/> call ran
    /// without a registered key. Page-side gating via
    /// <c>&lt;AuthorizeView&gt;</c> hides the controls when anonymous, but
    /// the model still owns the data refresh contract.
    /// </summary>
    private async Task OnAuthPublicKeyChangedAsync(CancellationToken cancellationToken)
    {
        _ = Auth.PublicKey;
        await RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// Probe DB existence + state-pill data. The encryption-state probe is
    /// shape-only (16-byte SQLite magic header on the first VERBATIM page) so
    /// it works without a registered key — useful on first load before the
    /// user authenticates.
    /// </summary>
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        RefreshOwnArmoredPubkey();

        Exists = await DatabaseService.ExistsDatabaseAsync(DatabaseName, cancellationToken);
        if (!Exists)
        {
            ItemCount = null;
            IsEncrypted = null;
            SizeBytes = 0;
            return;
        }

        // Magic-header probe: VERBATIM export returns raw OPFS bytes (plain
        // SQLite pages OR slot-format ciphertext under the registered key).
        // First 16 bytes of a plain DB are the SQLite magic; encrypted slot 0
        // is ChaCha20 ciphertext — ~2^-128 false-positive odds, the same
        // shape check the worker uses on `encryptDatabaseInPlace`.
        var bytes = await DatabaseService.ExportDatabaseAsync(
            DatabaseName, VfsExportMode.VERBATIM, default, cancellationToken);
        SizeBytes = bytes.Length;
        IsEncrypted = bytes.Length >= SqliteMagicHeader.Length
            && !bytes.AsSpan(0, SqliteMagicHeader.Length).SequenceEqual(SqliteMagicHeader);

        // Row count only readable when DB is plain or open under a registered
        // key (EF Core doesn't probe the worker's registry — install the key
        // first via Authenticate-and-open if encrypted).
        if (IsEncrypted == false || PrfService.HasCachedKeys())
        {
            try
            {
                await using var ctx = await ContextFactory.CreateDbContextAsync(cancellationToken);
                ItemCount = await ctx.TodoItems.CountAsync(cancellationToken);
            }
            catch
            {
                // Encrypted-but-key-not-registered, or DB schema mismatch.
                // The state pill still shows "Encrypted" via IsEncrypted.
                ItemCount = null;
            }
        }
        else
        {
            ItemCount = null;
        }
    }

    private async Task EncryptInPlaceAsync(CancellationToken cancellationToken)
    {
        var ck = TryGetCachedPubkeyBytes();
        if (ck is null)
        {
            throw new InvalidOperationException("No cached PRF pubkey — authenticate first.");
        }

        await DatabaseService.EncryptDatabaseInPlaceAsync(DatabaseName, ck, cancellationToken);
        // Worker registry now needs the key reinstalled before the next open
        // can route through the encrypted VFS — symmetric to PrfVfsTest's
        // post-rotate flow.
        var install = await DatabaseService.InstallEncryptionKeyAsync(DatabaseName, ck, cancellationToken);
        if (install is not VfsKeyInstallResult.MATCH)
        {
            throw new InvalidOperationException(
                $"Post-encrypt install returned {install} (expected MATCH).");
        }

        await RefreshAsync(cancellationToken);
        StatusModel.AddSuccess(Localizer["Status_Encrypted"], nameof(EncryptInPlace));
    }

    private async Task DecryptInPlaceAsync(CancellationToken cancellationToken)
    {
        await DatabaseService.DecryptDatabaseInPlaceAsync(DatabaseName, cancellationToken);
        await RefreshAsync(cancellationToken);
        StatusModel.AddSuccess(Localizer["Status_Decrypted"], nameof(DecryptInPlace));
    }

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        var bytes = await DatabaseService.ExportDatabaseAsync(
            DatabaseName, VfsExportMode.VERBATIM, default, cancellationToken);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var stateTag = IsEncrypted == true ? "encrypted" : "plain";
        var fileName = $"TodoDb-{stateTag}-{stamp}.db";

        await DownloadBytesAsync(bytes, fileName);
        SizeBytes = bytes.Length;
        StatusModel.AddSuccess(
            Localizer["Status_Exported", FormatSize(bytes.Length), fileName],
            nameof(Export));
    }

    private async Task ExportForRecipientAsync(CancellationToken cancellationToken)
    {
        var ck = TryGetPastedKeyBytes();
        if (ck is null)
        {
            throw new InvalidOperationException("Pasted recipient pubkey is missing or invalid.");
        }

        // Mode picks itself based on detected state: plain source → ENCRYPT,
        // encrypted source → REKEY. The worker rejects loud if the registry /
        // shape disagrees, so a state-mismatch never silently corrupts.
        var mode = IsEncrypted == true ? VfsExportMode.REKEY : VfsExportMode.ENCRYPT;
        var bytes = await DatabaseService.ExportDatabaseAsync(DatabaseName, mode, ck, cancellationToken);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = $"TodoDb-for-recipient-{stamp}.db";
        await DownloadBytesAsync(bytes, fileName);

        StatusModel.AddSuccess(
            Localizer["Status_ExportedForRecipient", FormatSize(bytes.Length), fileName],
            nameof(ExportForRecipient));
    }

    private async Task LockAsync(CancellationToken cancellationToken)
    {
        await DatabaseService.CloseDatabaseAsync(DatabaseName, cancellationToken);
        await DatabaseService.ClearEncryptionKeyAsync(DatabaseName, cancellationToken);
        Auth.ClearKeysCommand.Execute();
        OwnArmoredPubkey = null;
        await RefreshAsync(cancellationToken);
        StatusModel.AddWarning(Localizer["Status_Locked"], nameof(Lock));
    }

    private async Task WipeAsync(CancellationToken cancellationToken)
    {
        try { await DatabaseService.CloseDatabaseAsync(DatabaseName, cancellationToken); } catch { }
        try { await DatabaseService.ClearEncryptionKeyAsync(DatabaseName, cancellationToken); } catch { }
        await DatabaseService.DeleteDatabaseAsync(DatabaseName, cancellationToken);
        await RefreshAsync(cancellationToken);
        StatusModel.AddWarning(Localizer["Status_Wiped"], nameof(Wipe));
    }

    private byte[]? TryGetCachedPubkeyBytes()
    {
        var pubBase64 = PrfService.GetCachedPublicKey();
        if (pubBase64 is null) return null;
        var bytes = Convert.FromBase64String(pubBase64);
        return bytes.Length == 32 ? bytes : null;
    }

    /// <summary>
    /// Pure: parse the pasted PFA-armored pubkey to 32 raw bytes and update
    /// <see cref="PastedKeyError"/> on failure. Called from the
    /// <see cref="ExportForRecipient"/> guard so the button enables only when
    /// a valid recipient pubkey is in the textfield.
    /// </summary>
    private byte[]? TryGetPastedKeyBytes()
    {
        if (string.IsNullOrWhiteSpace(PastedArmoredPubkey))
        {
            PastedKeyError = null;
            return null;
        }

        var base64 = PrfArmor.UnArmorPublicKey(PastedArmoredPubkey);
        if (base64 is null)
        {
            PastedKeyError = Localizer["Error_PastedPubkey_BadArmor"];
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            PastedKeyError = Localizer["Error_PastedPubkey_BadBase64"];
            return null;
        }

        if (bytes.Length != 32)
        {
            PastedKeyError = Localizer["Error_PastedPubkey_WrongLength", bytes.Length];
            return null;
        }

        PastedKeyError = null;
        return bytes;
    }

    private void RefreshOwnArmoredPubkey()
    {
        var pub = PrfService.GetCachedPublicKey();
        if (pub is null)
        {
            OwnArmoredPubkey = null;
            return;
        }
        OwnArmoredPubkey = PrfArmor.ArmorPublicKey(pub, new PublicKeyMetadata
        {
            Name = "Database encryption demo",
            Comment = $"Domain: {DomainId}",
            Created = DateOnly.FromDateTime(DateTime.UtcNow),
        });
    }

    /// <summary>
    /// Triggers the browser download. Implemented by the page-side partial
    /// (component-trigger) — the model layer stays free of JSInterop.
    /// </summary>
    [ObservableComponentTriggerAsync]
    public partial PendingDownload? PendingDownload { get; set; }

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

    private string FormatStateError(Exception ex) => ex switch
    {
        OperationCanceledException => Localizer["Status_OperationCancelled"],
        _ => Localizer["Error_Operation", ex.Message],
    };
}

public sealed record PendingDownload(byte[] Bytes, string FileName, TaskCompletionSource Done);
