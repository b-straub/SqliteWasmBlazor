namespace SqliteWasmBlazor.Crypto.Abstractions.Models;

/// <summary>
/// Result wrapper for PRF/PseudoPRF operations.
/// </summary>
/// <typeparam name="T">The value type on success</typeparam>
public sealed record PrfResult<T>
{
    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The result value (only present if Success is true).
    /// </summary>
    public T? Value { get; init; }

    /// <summary>
    /// The error code (only present if Success is false and not Cancelled).
    /// </summary>
    public PrfErrorCode? ErrorCode { get; init; }

    /// <summary>
    /// Whether the user cancelled the operation.
    /// </summary>
    public bool Cancelled { get; init; }

    /// <summary>
    /// Gets the user-friendly error message for the error code.
    /// </summary>
    public string? Error => ErrorCode.HasValue
        ? PrfErrorMessages.GetMessage(ErrorCode.Value)
        : null;

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static PrfResult<T> Ok(T value) => new()
    {
        Success = true,
        Value = value
    };

    /// <summary>
    /// Creates a failed result with an error code.
    /// </summary>
    public static PrfResult<T> Fail(PrfErrorCode errorCode) => new()
    {
        Success = false,
        ErrorCode = errorCode
    };

    /// <summary>
    /// Creates a cancelled result (user cancelled the operation).
    /// </summary>
    public static PrfResult<T> UserCancelled() => new()
    {
        Success = false,
        Cancelled = true
    };
}

/// <summary>
/// X25519 key pair for asymmetric encryption.
/// </summary>
/// <param name="PrivateKeyBase64">The private key (Base64, 32 bytes) - keep secure!</param>
/// <param name="PublicKeyBase64">The public key (Base64, 32 bytes) - can be shared.</param>
public sealed record KeyPair(
    string PrivateKeyBase64,
    string PublicKeyBase64
);
