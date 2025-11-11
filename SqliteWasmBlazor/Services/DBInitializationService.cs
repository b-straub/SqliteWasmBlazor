// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

namespace SqliteWasmBlazor;

/// <summary>
/// Default implementation of database initialization service.
/// Tracks database initialization status and error messages.
/// </summary>
public class DBInitializationService : IDBInitializationService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DBInitializationService"/> class.
    /// </summary>
    /// <param name="errorMessage">Optional initial error message.</param>
    public DBInitializationService(string? errorMessage = null)
    {
        ErrorMessage = errorMessage;
    }

    /// <inheritdoc />
    public string? ErrorMessage { get; set; }
}
