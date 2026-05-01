// @sqlitewasmblazor/crypto-core — Content encryption key (CEK) management
import { generateRandomBytes } from './utils.js';
import { KEY_LENGTH } from './types.js';
import { encryptAesGcm, decryptAesGcm } from './aesGcm.js';
/**
 * Generate a random 32-byte content encryption key (CEK).
 * Caller must clearBytes the result when done.
 */
export function generateContentKey() {
    return generateRandomBytes(KEY_LENGTH);
}
/**
 * Wrap (encrypt) a CEK with a wrapping key using AES-256-GCM.
 */
export async function wrapContentKey(contentKey, wrappingKey) {
    return encryptAesGcm(contentKey, wrappingKey);
}
/**
 * Unwrap (decrypt) a CEK from a wrapped blob.
 * Returns the 32-byte CEK. Caller must clearBytes when done.
 */
export async function unwrapContentKey(wrapped, wrappingKey) {
    return decryptAesGcm(wrapped, wrappingKey);
}
//# sourceMappingURL=keyWrapping.js.map