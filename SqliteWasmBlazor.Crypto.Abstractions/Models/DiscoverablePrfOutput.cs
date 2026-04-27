namespace SqliteWasmBlazor.Crypto.Abstractions.Models;

/// <summary>
/// Result from discoverable PRF authentication containing the credential ID and raw PRF output.
/// </summary>
/// <param name="CredentialId">The credential ID (Base64) of the selected credential.</param>
/// <param name="PrfOutput">The raw PRF output (Base64) from WebAuthn.</param>
public sealed record DiscoverablePrfOutput(
    string CredentialId,
    string PrfOutput
);
