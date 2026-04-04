/**
 * Core type definitions for SqliteWasmBlazor crypto layer.
 */

export interface EncryptedMessage {
    ephemeralPublicKey: string; // Base64
    ciphertext: string;         // Base64
    nonce: string;              // Base64
}

export interface SymmetricEncryptedMessage {
    ciphertext: string; // Base64
    nonce: string;      // Base64
}

export interface KeyCacheConfig {
    strategy: 'none' | 'session' | 'timed';
    ttlMs: number;
}

export enum CryptoErrorCode {
    Unknown = 'Unknown',
    AuthenticationTagMismatch = 'AuthenticationTagMismatch',
    InvalidData = 'InvalidData',
    KeyDerivationFailed = 'KeyDerivationFailed',
    EncryptionFailed = 'EncryptionFailed',
    DecryptionFailed = 'DecryptionFailed',
}
