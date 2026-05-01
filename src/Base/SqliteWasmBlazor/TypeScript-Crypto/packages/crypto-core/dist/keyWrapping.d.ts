import type { SymmetricEncryptedData } from './types.js';
/**
 * Generate a random 32-byte content encryption key (CEK).
 * Caller must clearBytes the result when done.
 */
export declare function generateContentKey(): Uint8Array;
/**
 * Wrap (encrypt) a CEK with a wrapping key using AES-256-GCM.
 */
export declare function wrapContentKey(contentKey: Uint8Array, wrappingKey: Uint8Array): Promise<SymmetricEncryptedData>;
/**
 * Unwrap (decrypt) a CEK from a wrapped blob.
 * Returns the 32-byte CEK. Caller must clearBytes when done.
 */
export declare function unwrapContentKey(wrapped: SymmetricEncryptedData, wrappingKey: Uint8Array): Promise<Uint8Array>;
//# sourceMappingURL=keyWrapping.d.ts.map