export declare const NONCE_LENGTH_AES = 12;
export declare const KEY_LENGTH = 32;
/**
 * Error codes matching C# PrfErrorCode enum.
 * String values for JSON compatibility.
 */
export declare enum PrfErrorCode {
    Unknown = "Unknown",
    NotSupported = "NotSupported",
    PrfNotSupported = "PrfNotSupported",
    CredentialNotFound = "CredentialNotFound",
    AuthenticationTagMismatch = "AuthenticationTagMismatch",
    InvalidData = "InvalidData",
    KeyDerivationFailed = "KeyDerivationFailed",
    EncryptionFailed = "EncryptionFailed",
    DecryptionFailed = "DecryptionFailed",
    RegistrationFailed = "RegistrationFailed",
    InvalidPublicKey = "InvalidPublicKey",
    InvalidPrivateKey = "InvalidPrivateKey",
    SigningFailed = "SigningFailed",
    VerificationFailed = "VerificationFailed",
    IncompatibleFormat = "IncompatibleFormat"
}
/**
 * Result wrapper matching C# PrfResult<T>.
 */
export interface PrfResult<T> {
    success: boolean;
    value?: T;
    errorCode?: PrfErrorCode;
    cancelled?: boolean;
}
/**
 * Factory functions for PrfResult — mirrors C# static methods.
 */
export declare const PrfResultUtil: {
    readonly ok: <T>(value: T) => PrfResult<T>;
    readonly fail: <T>(errorCode: PrfErrorCode) => PrfResult<T>;
    readonly cancelled: <T>() => PrfResult<T>;
};
/**
 * A key pair (private + public). Matches C# KeyPair record.
 */
export interface KeyPair {
    privateKey: Uint8Array;
    publicKey: Uint8Array;
}
/**
 * Public keys only (X25519 + Ed25519). Matches C# DualKeyPair record.
 */
export interface DualKeyPair {
    x25519PublicKey: Uint8Array;
    ed25519PublicKey: Uint8Array;
}
/**
 * Full dual key pair (both private + public). Matches C# DualKeyPairFull record.
 */
export interface DualKeyPairFull {
    x25519PrivateKey: Uint8Array;
    x25519PublicKey: Uint8Array;
    ed25519PrivateKey: Uint8Array;
    ed25519PublicKey: Uint8Array;
}
/**
 * AES-256-GCM encrypted data (nonce + ciphertext).
 * Matches C# SymmetricEncryptedData record.
 */
export interface SymmetricEncryptedData {
    ciphertext: Uint8Array;
    nonce: Uint8Array;
}
/**
 * ECIES encrypted data (X25519 + AES-GCM).
 * Matches C# AsymmetricEncryptedData record.
 */
export interface AsymmetricEncryptedData {
    ephemeralPublicKey: Uint8Array;
    ciphertext: Uint8Array;
    nonce: Uint8Array;
}
/**
 * A content encryption key (CEK) wrapped for a specific group member.
 * Matches C# WrappedKey record.
 */
export interface WrappedKey {
    memberPublicKey: Uint8Array;
    wrappedContentKey: SymmetricEncryptedData;
}
/**
 * Complete key bundle for a group — contains wrapped CEKs for all members.
 * Matches C# GroupKeyBundle record.
 */
export interface GroupKeyBundle {
    groupContext: string;
    keyVersion: number;
    adminPublicKey: Uint8Array;
    memberKeys: WrappedKey[];
}
/**
 * An encrypted payload within a group, with tamper detection metadata.
 * Matches C# GroupEncryptedData record.
 */
export interface GroupEncryptedData {
    groupContext: string;
    keyVersion: number;
    encrypted: SymmetricEncryptedData;
    senderPublicKey: Uint8Array;
    envelopeSignature: Uint8Array;
}
//# sourceMappingURL=types.d.ts.map