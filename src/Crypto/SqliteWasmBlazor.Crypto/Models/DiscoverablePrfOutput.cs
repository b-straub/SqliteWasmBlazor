namespace SqliteWasmBlazor.Crypto.Abstractions.Models;

/// <summary>
/// Result from discoverable PRF authentication containing the credential ID and raw PRF output.
/// </summary>
/// <param name="CredentialId">The credential ID (Base64) of the selected credential.</param>
/// <param name="PrfOutput">
/// The raw PRF output bytes (32 bytes) from WebAuthn. Wire format is Base64 —
/// <see cref="System.Text.Json.JsonSerializer"/> defaults to Base64 encoding for
/// <see cref="byte"/>[] properties, so the JS side keeps emitting the same JSON
/// shape. Secret material — never lands in a managed <see cref="string"/> (P21).
/// </param>
public sealed record DiscoverablePrfOutput(
    string CredentialId,
    byte[] PrfOutput
);
