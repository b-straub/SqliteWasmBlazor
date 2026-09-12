// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Discriminated payload for a failed boot stage. Implementations are concrete
/// records — pattern-match on the type to choose UI / remedy. Library ships
/// transport- and schema-related failures; downstream packages (e.g.
/// <c>SqliteWasmBlazor.CryptoSync</c>) define their own without taking a
/// dependency on this assembly's internals.
/// </summary>
public interface IDbInitFailure
{
    /// <summary>The OPFS database name the failure relates to (or empty if pre-DB).</summary>
    string DatabaseName { get; }

    /// <summary>
    /// Human-readable fallback message. Apps may render this directly or
    /// substitute their own copy keyed off the concrete failure type.
    /// </summary>
    string DefaultMessage { get; }
}
