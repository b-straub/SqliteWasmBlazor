import type { AsymmetricEncryptedData } from './types.js';
/**
 * Encrypt with ECIES: ephemeral X25519 key agreement + HKDF + AES-256-GCM.
 */
export declare function encryptAsymmetric(plaintext: Uint8Array, recipientPublicKey: Uint8Array): Promise<AsymmetricEncryptedData>;
/**
 * Decrypt with ECIES: X25519 key agreement + HKDF + AES-256-GCM.
 */
export declare function decryptAsymmetric(encrypted: AsymmetricEncryptedData, privateKey: Uint8Array): Promise<Uint8Array>;
//# sourceMappingURL=ecies.d.ts.map