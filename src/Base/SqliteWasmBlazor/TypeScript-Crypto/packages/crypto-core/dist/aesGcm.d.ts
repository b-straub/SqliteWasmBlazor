import type { SymmetricEncryptedData } from './types.js';
/**
 * Encrypt with AES-256-GCM.
 * @param plaintext - data to encrypt
 * @param key - 32-byte AES key
 * @param aad - optional additional authenticated data (raw bytes)
 */
export declare function encryptAesGcm(plaintext: Uint8Array, key: Uint8Array, aad?: Uint8Array): Promise<SymmetricEncryptedData>;
/**
 * Decrypt with AES-256-GCM.
 * @param encrypted - ciphertext + nonce
 * @param key - 32-byte AES key
 * @param aad - optional additional authenticated data (must match encryption)
 */
export declare function decryptAesGcm(encrypted: SymmetricEncryptedData, key: Uint8Array, aad?: Uint8Array): Promise<Uint8Array>;
//# sourceMappingURL=aesGcm.d.ts.map