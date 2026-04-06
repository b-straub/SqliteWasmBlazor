namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Base for ALL entities in a CryptoSync app. Every table syncs. Every table gets a _crypto_ shadow.
/// The SharingScope determines WHO can decrypt the row (Public/Shared/Client).
/// </summary>
public interface ISyncableEntity
{
    Guid Id { get; set; }

    /// <summary>Visibility scope — determines who gets the decryption key.</summary>
    SharingScope SharingScope { get; set; }

    /// <summary>Scope identifier for key lookup (e.g. "list-{guid}" for a shared shopping list).</summary>
    string SharingId { get; set; }

    DateTime UpdatedAt { get; set; }
    bool IsDeleted { get; set; }
    DateTime? DeletedAt { get; set; }
}

/// <summary>
/// Visibility scopes. All are encrypted — the difference is WHO gets the scope key.
/// </summary>
public enum SharingScope
{
    /// <summary>Encrypted, ALL verified contacts get the scope key.</summary>
    Public = 0,

    /// <summary>Encrypted, only selected contacts get the scope key.</summary>
    Shared = 1,

    /// <summary>Encrypted, only this client's key.</summary>
    Client = 2
}
