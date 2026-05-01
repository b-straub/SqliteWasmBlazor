using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.Abstractions.Services;

/// <summary>
/// Context for signing API requests.
/// </summary>
public sealed record SigningContext(
    string PublicKey,
    string Salt,
    Func<string, string, Task<PrfResult<string>>> SignAsync);
