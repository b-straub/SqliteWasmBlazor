namespace SqliteWasmBlazor.Crypto.Abstractions.Models;

/// <summary>
/// Error codes for PRF/PseudoPRF operations.
/// </summary>
public enum PrfErrorCode
{
    UNKNOWN,
    NOT_SUPPORTED,
    PRF_NOT_SUPPORTED,
    CREDENTIAL_NOT_FOUND,
    AUTHENTICATION_TAG_MISMATCH,
    INVALID_DATA,
    KEY_DERIVATION_FAILED,
    ENCRYPTION_FAILED,
    DECRYPTION_FAILED,
    REGISTRATION_FAILED,
    INVALID_PUBLIC_KEY,
    INVALID_PRIVATE_KEY,
    SIGNING_FAILED,
    VERIFICATION_FAILED,
    INCOMPATIBLE_FORMAT,
}

/// <summary>
/// Provides user-friendly error messages for PRF error codes.
/// </summary>
public static class PrfErrorMessages
{
    /// <summary>
    /// Gets the user-friendly error message for the given error code.
    /// </summary>
    public static string GetMessage(PrfErrorCode errorCode) => errorCode switch
    {
        PrfErrorCode.NOT_SUPPORTED =>
            "This operation is not supported by the current crypto provider.",
        PrfErrorCode.PRF_NOT_SUPPORTED =>
            "The selected passkey does not support PRF extension. Please select a passkey that was created with PRF support, or register a new one.",
        PrfErrorCode.CREDENTIAL_NOT_FOUND =>
            "The credential was not found. It may have been deleted or is not available on this device.",
        PrfErrorCode.AUTHENTICATION_TAG_MISMATCH =>
            "Decryption failed: wrong key or corrupted data. This data was encrypted with a different key.",
        PrfErrorCode.INVALID_DATA =>
            "The data is invalid or corrupted.",
        PrfErrorCode.KEY_DERIVATION_FAILED =>
            "Key derivation failed. Please try again.",
        PrfErrorCode.ENCRYPTION_FAILED =>
            "Encryption failed. Please try again.",
        PrfErrorCode.DECRYPTION_FAILED =>
            "Decryption failed. The data may be corrupted.",
        PrfErrorCode.REGISTRATION_FAILED =>
            "Passkey registration failed. Please try again.",
        PrfErrorCode.INVALID_PUBLIC_KEY =>
            "The public key is invalid or malformed.",
        PrfErrorCode.INVALID_PRIVATE_KEY =>
            "The private key is invalid or malformed.",
        PrfErrorCode.SIGNING_FAILED =>
            "Signing failed. Please try again.",
        PrfErrorCode.VERIFICATION_FAILED =>
            "Signature verification failed. The signature is invalid or the data has been tampered with.",
        PrfErrorCode.INCOMPATIBLE_FORMAT =>
            "This message was encrypted with an older version and cannot be decrypted. Please ask the sender to re-encrypt.",
        _ =>
            "An unknown error occurred."
    };
}
