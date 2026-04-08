using MessagePack;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Per-call metadata shipped from the C# host to the worker alongside every
/// V2 bulk export/import call. Carries everything the worker needs to route
/// the staged-apply pass (system tables first, then domain tables), derive
/// content keys without round-tripping through the host, and stamp shadow
/// rows with the local client contact id.
///
/// <para>
/// This type is PURELY a transport shape — no behavior. It is hand-written
/// (not generated) so that future additions (e.g. ephemeral ECIES unwrap
/// keys for two-actor scenarios) can land without a generator roundtrip.
/// Stages 5 and 6 consume it; the caller builds it from the user's
/// <c>SystemTableRegistry</c> (generated in user-ns), <see cref="DeviceIdentityService"/>,
/// and the session's X25519 private key.
/// </para>
///
/// <para>
/// <b>Security note.</b> <see cref="ClientPrivateKey"/> is the session X25519
/// private key and is sensitive. Callers MUST zero the backing buffer after
/// the worker call returns (see <see cref="Clear"/>) and must not persist
/// the header beyond the single bulk call it was built for.
/// </para>
/// </summary>
[MessagePackObject]
public sealed class V2CryptoHeader
{
    /// <summary>Wire format version. Bumped on schema changes.</summary>
    [Key(0)]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Table names (DbSet names) of all system tables in the current context,
    /// sourced from the generated <c>SystemTableRegistry</c>. The worker uses
    /// this list to partition incoming groups into the stage-1 (system) pass
    /// and the stage-2 (domain) pass.
    /// </summary>
    [Key(1)]
    public List<string> SystemTables { get; set; } = [];

    /// <summary>
    /// Table name of the <c>SharingKey</c> system table (always
    /// <see cref="DefaultSharingTableName"/> today, but kept explicit on the
    /// header so the worker does not hard-code a library-internal name).
    /// The worker looks up per-scope content keys via this table during the
    /// stage-2 pass for rows this device did not originate.
    /// </summary>
    [Key(2)]
    public string SharingTableName { get; set; } = DefaultSharingTableName;

    /// <summary>
    /// This device's own <see cref="TrustedContact.Id"/>. Used by the worker
    /// to stamp locally-originated shadow rows and to short-circuit content
    /// key lookup when rows are already scoped to this client.
    /// </summary>
    [Key(3)]
    public Guid ClientContactId { get; set; }

    /// <summary>
    /// This session's X25519 private key (32 bytes). Used by the worker to
    /// derive system / domain content keys via <see cref="KeyDerivation"/>
    /// without needing to round-trip back through the host. Sensitive — see
    /// the type-level security note.
    /// </summary>
    [Key(4)]
    public byte[] ClientPrivateKey { get; set; } = [];

    /// <summary>
    /// Canonical DbSet name of the library's <see cref="SharingKey"/> table.
    /// Matches <c>CryptoSyncContextBase.SharingKeys</c>.
    /// </summary>
    public const string DefaultSharingTableName = "SharingKeys";

    /// <summary>
    /// Construct a header from the primary inputs in one call. Provided as a
    /// convenience for call sites that already have all four pieces in hand;
    /// tests and the <see cref="SyncOrchestrator"/> both go through this.
    /// </summary>
    public static V2CryptoHeader Create(
        IEnumerable<string> systemTables,
        Guid clientContactId,
        ReadOnlySpan<byte> clientPrivateKey,
        string? sharingTableName = null)
    {
        if (systemTables is null)
        {
            throw new ArgumentNullException(nameof(systemTables));
        }

        if (clientPrivateKey.Length != 32)
        {
            throw new ArgumentException(
                $"ClientPrivateKey must be 32 bytes (X25519), got {clientPrivateKey.Length}.",
                nameof(clientPrivateKey));
        }

        return new V2CryptoHeader
        {
            Version = 1,
            SystemTables = [.. systemTables],
            SharingTableName = sharingTableName ?? DefaultSharingTableName,
            ClientContactId = clientContactId,
            ClientPrivateKey = clientPrivateKey.ToArray()
        };
    }

    /// <summary>
    /// Zero the sensitive <see cref="ClientPrivateKey"/> buffer. Call as soon
    /// as the header is no longer needed (typically in a <c>finally</c> right
    /// after the worker bulk call returns).
    /// </summary>
    public void Clear()
    {
        if (ClientPrivateKey.Length > 0)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(ClientPrivateKey);
        }
    }

    /// <summary>
    /// True if this table name is a system table for the purposes of
    /// staged-apply routing. Case-sensitive — table names always use the
    /// generator's DbSet-name casing.
    /// </summary>
    public bool IsSystemTable(string tableName)
    {
        foreach (var t in SystemTables)
        {
            if (t == tableName)
            {
                return true;
            }
        }
        return false;
    }
}
