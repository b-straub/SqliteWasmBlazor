// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Service for tracking database initialization status and errors.
/// </summary>
public interface IDBInitializationService
{
    /// <summary>
    /// Gets or sets the error message if database initialization failed.
    /// </summary>
    string? ErrorMessage { get; set; }
}
