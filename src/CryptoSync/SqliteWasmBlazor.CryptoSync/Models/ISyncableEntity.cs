namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Base class for ALL entities in a CryptoSync app. Every table syncs. Every table gets a _crypto_ shadow.
/// Domain entities inherit this and add their own properties.
/// </summary>
public abstract class SyncableEntity
{
    public Guid Id { get; set; }

    /// <summary>
    /// Visibility scope — determines who gets the decryption key. Defaults
    /// to <see cref="CryptoSync.SharingScope.CLIENT"/> (privacy-by-default): a brand-new
    /// row is private to the creator's self-group until the caller
    /// explicitly widens scope to <see cref="CryptoSync.SharingScope.PUBLIC"/> or
    /// hands the row to a named <see cref="CryptoSync.SharingScope.SHARED"/> group.
    /// </summary>
    public SharingScope SharingScope { get; set; } = SharingScope.CLIENT;

    /// <summary>Scope identifier for key lookup (e.g. "list-{guid}" for a shared shopping list).</summary>
    public string SharingId { get; set; } = "";

    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}

/// <summary>
/// Visibility scopes. Determines WHO gets the group CEK that the row's
/// shadow copy is encrypted under for sync forwarding. (At-rest pages are
/// already protected by the PRF-keyed VFS regardless of scope.)
/// </summary>
public enum SharingScope
{
    /// <summary>ALL verified contacts get the scope key.</summary>
    PUBLIC = 0,

    /// <summary>Only selected contacts get the scope key.</summary>
    SHARED = 1,

    /// <summary>Only this client's key.</summary>
    CLIENT = 2
}
