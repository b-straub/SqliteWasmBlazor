namespace SqliteWasmBlazor.Crypto.Abstractions.Models;

/// <summary>
/// A signed message with its Ed25519 signature.
/// </summary>
/// <param name="Message">The original message content.</param>
/// <param name="Signature">Base64-encoded Ed25519 signature (64 bytes).</param>
/// <param name="PublicKey">Base64-encoded Ed25519 public key for verification.</param>
/// <param name="TimestampUnix">Unix timestamp (seconds since 1970-01-01 UTC) when the message was signed.</param>
public sealed record SignedData(
    string Message,
    string Signature,
    string PublicKey,
    long TimestampUnix
);
