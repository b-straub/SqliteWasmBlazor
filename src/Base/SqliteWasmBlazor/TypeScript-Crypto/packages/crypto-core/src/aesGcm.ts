// @sqlitewasmblazor/crypto-core — AES-256-GCM via SubtleCrypto (hardware accelerated)

import { toBuffer, generateRandomBytes } from './utils.js';
import { NONCE_LENGTH_AES, KEY_LENGTH } from './types.js';
import type { SymmetricEncryptedData } from './types.js';

/**
 * Encrypt with AES-256-GCM.
 * @param plaintext - data to encrypt
 * @param key - 32-byte AES key
 * @param aad - optional additional authenticated data (raw bytes)
 */
export async function encryptAesGcm(
    plaintext: Uint8Array,
    key: Uint8Array,
    aad?: Uint8Array
): Promise<SymmetricEncryptedData> {
    if (key.length !== KEY_LENGTH) {
        throw new Error(`Invalid key length: expected ${KEY_LENGTH}, got ${key.length}`);
    }

    const cryptoKey = await crypto.subtle.importKey(
        'raw', toBuffer(key), { name: 'AES-GCM' }, false, ['encrypt']
    );

    const nonce = generateRandomBytes(NONCE_LENGTH_AES);

    const params: AesGcmParams = { name: 'AES-GCM', iv: toBuffer(nonce) };
    if (aad) {
        params.additionalData = toBuffer(aad);
    }

    const ciphertext = await crypto.subtle.encrypt(params, cryptoKey, toBuffer(plaintext));

    return {
        ciphertext: new Uint8Array(ciphertext),
        nonce,
    };
}

/**
 * Decrypt with AES-256-GCM.
 * @param encrypted - ciphertext + nonce
 * @param key - 32-byte AES key
 * @param aad - optional additional authenticated data (must match encryption)
 */
export async function decryptAesGcm(
    encrypted: SymmetricEncryptedData,
    key: Uint8Array,
    aad?: Uint8Array
): Promise<Uint8Array> {
    if (key.length !== KEY_LENGTH) {
        throw new Error(`Invalid key length: expected ${KEY_LENGTH}, got ${key.length}`);
    }

    if (encrypted.nonce.length !== NONCE_LENGTH_AES) {
        throw new Error(`Invalid nonce length: expected ${NONCE_LENGTH_AES}, got ${encrypted.nonce.length}`);
    }

    const cryptoKey = await crypto.subtle.importKey(
        'raw', toBuffer(key), { name: 'AES-GCM' }, false, ['decrypt']
    );

    const params: AesGcmParams = { name: 'AES-GCM', iv: toBuffer(encrypted.nonce) };
    if (aad) {
        params.additionalData = toBuffer(aad);
    }

    const plaintext = await crypto.subtle.decrypt(params, cryptoKey, toBuffer(encrypted.ciphertext));

    return new Uint8Array(plaintext);
}
