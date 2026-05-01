namespace SqliteWasmBlazor.Crypto.Abstractions.Services;

/// <summary>
/// Provides signing context for authenticated API requests.
/// Bridges authentication state (keys) with the signing service.
/// </summary>
public interface ISigningContextProvider
{
    SigningContext CreateSigningContext();
}
