namespace SqliteWasmBlazor.Crypto.Abstractions.Models;

/// <summary>
/// Represents a WebAuthn credential with PRF support.
/// </summary>
/// <param name="Id">The credential ID (URL-safe Base64).</param>
/// <param name="RawId">The raw credential ID (standard Base64).</param>
public sealed record PrfCredential(
    string Id,
    string RawId
);
