// @sqlitewasmblazor/crypto-core — shared types
// All types use Uint8Array for binary data (no Base64 strings)
// ============================================================
// CONSTANTS
// ============================================================
export const NONCE_LENGTH_AES = 12;
export const KEY_LENGTH = 32;
// ============================================================
// ERROR HANDLING
// ============================================================
/**
 * Error codes matching C# PrfErrorCode enum.
 * String values for JSON compatibility.
 */
export var PrfErrorCode;
(function (PrfErrorCode) {
    PrfErrorCode["Unknown"] = "Unknown";
    PrfErrorCode["NotSupported"] = "NotSupported";
    PrfErrorCode["PrfNotSupported"] = "PrfNotSupported";
    PrfErrorCode["CredentialNotFound"] = "CredentialNotFound";
    PrfErrorCode["AuthenticationTagMismatch"] = "AuthenticationTagMismatch";
    PrfErrorCode["InvalidData"] = "InvalidData";
    PrfErrorCode["KeyDerivationFailed"] = "KeyDerivationFailed";
    PrfErrorCode["EncryptionFailed"] = "EncryptionFailed";
    PrfErrorCode["DecryptionFailed"] = "DecryptionFailed";
    PrfErrorCode["RegistrationFailed"] = "RegistrationFailed";
    PrfErrorCode["InvalidPublicKey"] = "InvalidPublicKey";
    PrfErrorCode["InvalidPrivateKey"] = "InvalidPrivateKey";
    PrfErrorCode["SigningFailed"] = "SigningFailed";
    PrfErrorCode["VerificationFailed"] = "VerificationFailed";
    PrfErrorCode["IncompatibleFormat"] = "IncompatibleFormat";
})(PrfErrorCode || (PrfErrorCode = {}));
/**
 * Factory functions for PrfResult — mirrors C# static methods.
 */
export const PrfResultUtil = {
    ok: (value) => ({ success: true, value }),
    fail: (errorCode) => ({ success: false, errorCode }),
    cancelled: () => ({ success: false, cancelled: true }),
};
//# sourceMappingURL=types.js.map