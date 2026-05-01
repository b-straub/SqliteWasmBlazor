// @sqlitewasmblazor/crypto-core — ChaCha20-Poly1305 AEAD (sync, via @awasm/noble WASM-SIMD)

import { chacha20poly1305 } from '@awasm/noble';
import { generateRandomBytes } from './utils.js';
import { KEY_LENGTH } from './types.js';
import type { SymmetricEncryptedData } from './types.js';

const NONCE_LENGTH = 12;

export function encryptChaCha20Poly1305(
    plaintext: Uint8Array,
    key: Uint8Array,
    associatedData?: Uint8Array
): SymmetricEncryptedData {
    if (key.length !== KEY_LENGTH) {
        throw new Error(`Invalid key length: expected ${KEY_LENGTH}, got ${key.length}`);
    }

    const nonce = generateRandomBytes(NONCE_LENGTH);
    const cipher = associatedData
        ? chacha20poly1305(key, nonce, associatedData)
        : chacha20poly1305(key, nonce);
    const ciphertext = cipher.encrypt(plaintext);

    return { ciphertext, nonce };
}

export function decryptChaCha20Poly1305(
    encrypted: SymmetricEncryptedData,
    key: Uint8Array,
    associatedData?: Uint8Array
): Uint8Array {
    if (key.length !== KEY_LENGTH) {
        throw new Error(`Invalid key length: expected ${KEY_LENGTH}, got ${key.length}`);
    }

    if (encrypted.nonce.length !== NONCE_LENGTH) {
        throw new Error(`Invalid nonce length: expected ${NONCE_LENGTH}, got ${encrypted.nonce.length}`);
    }

    const cipher = associatedData
        ? chacha20poly1305(key, encrypted.nonce, associatedData)
        : chacha20poly1305(key, encrypted.nonce);
    return cipher.decrypt(encrypted.ciphertext);
}
