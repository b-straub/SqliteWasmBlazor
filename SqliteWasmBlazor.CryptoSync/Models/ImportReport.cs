using MessagePack;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Structured result from a V2 encrypted delta import. The worker returns
/// this instead of a raw row count so the caller can surface tamper
/// detection failures and permission violations to the UI or audit log.
///
/// <para>
/// Non-fatal errors (per-row AAD mismatch, per-row permission denial) skip
/// the affected row but allow the rest of the import to proceed. Fatal
/// errors (batch signature invalid, CEK unwrap failed for a group) abort
/// the affected scope — either the entire delta or all rows in that group.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class ImportReport
{
    /// <summary>Number of rows successfully decrypted and applied to the open table.</summary>
    [Key(0)]
    public int RowsImported { get; set; }

    /// <summary>Number of rows skipped due to tamper detection or permission failures.</summary>
    [Key(1)]
    public int RowsSkipped { get; set; }

    /// <summary>Number of tombstone rows (IsDeleted=true) that were hard-deleted from both open and shadow tables.</summary>
    [Key(3)]
    public int RowsDeleted { get; set; }

    /// <summary>
    /// All errors encountered during import. Empty when the import is fully
    /// successful. Ordered by detection order (batch-level first, then
    /// group-level, then per-row).
    /// </summary>
    [Key(2)]
    public List<ImportError> Errors { get; set; } = [];
}

/// <summary>
/// One error from the import. Covers all three tamper detection layers
/// plus permission enforcement.
/// </summary>
[MessagePackObject]
public sealed class ImportError
{
    /// <summary>Error classification — see <see cref="ImportErrorCode"/>.</summary>
    [Key(0)]
    public ImportErrorCode Code { get; set; }

    /// <summary>
    /// Table name affected, or <c>"envelope"</c> for batch-level signature
    /// failures, or <c>"group"</c> for CEK unwrap failures.
    /// </summary>
    [Key(1)]
    public string Table { get; set; } = string.Empty;

    /// <summary>
    /// Row Id as string (Guid), or empty for batch/group-level errors.
    /// </summary>
    [Key(2)]
    public string RowId { get; set; } = string.Empty;

    /// <summary>Group identifier (SharingId or GroupContext) for context.</summary>
    [Key(3)]
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Human-readable detail for diagnostics / audit log.</summary>
    [Key(4)]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Error codes for the unified tamper detection + permission violation log.
/// The three tamper detection layers map directly to the PDF spec; permission
/// codes match <c>SyncPermission</c> enforcement.
/// </summary>
public enum ImportErrorCode
{
    // --- Tamper Detection ---

    /// <summary>Layer 2 — Ed25519 envelope signature invalid. Fatal: entire delta rejected.</summary>
    TAMPER_SIGNATURE_INVALID = 1,

    /// <summary>Layer 3 — AES-GCM auth tag failed on CEK unwrap. Fatal for the group.</summary>
    TAMPER_CEK_UNWRAP_FAILED = 2,

    /// <summary>Layer 1 — AES-GCM AAD mismatch (groupContext or keyVersion tampered). Per-row skip.</summary>
    TAMPER_AAD_MISMATCH = 3,

    /// <summary>General AES-GCM decryption failure (corrupt ciphertext). Per-row skip.</summary>
    TAMPER_DECRYPT_FAILED = 4,

    // --- Permission Violations ---

    /// <summary>Sender's role does not permit insert on this table.</summary>
    PERMISSION_INSERT_DENIED = 10,

    /// <summary>Sender's role does not permit update on this table.</summary>
    PERMISSION_UPDATE_DENIED = 11,

    /// <summary>Sender's role does not permit delete on this table.</summary>
    PERMISSION_DELETE_DENIED = 12,

    /// <summary>Sender's role cannot update a readonly column.</summary>
    PERMISSION_COLUMN_READONLY = 13,

    /// <summary>Sender has no valid ShareTarget/permission credential chain for this group.</summary>
    PERMISSION_SENDER_UNAUTHORIZED = 14,

    // --- Routing ---

    /// <summary>No ShareTarget found for this group — cannot derive CEK.</summary>
    UNKNOWN_GROUP = 20,

    /// <summary>Catch-all for unexpected failures.</summary>
    UNKNOWN_ERROR = 99
}
