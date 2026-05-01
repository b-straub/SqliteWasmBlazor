// @sqlitewasmblazor/crypto-core — ECIES (X25519 + HKDF + AES-256-GCM)
import { sha256 } from '@awasm/noble';
import { hkdf } from '@awasm/noble/hkdf.js';
import { clearBytes, toBuffer, generateRandomBytes } from './utils.js';
import { KEY_LENGTH, NONCE_LENGTH_AES } from './types.js';
import { x25519SharedSecret, getX25519PublicKey } from './x25519.js';
const encoder = new TextEncoder();
const ECIES_INFO = encoder.encode('ecies-aes-gcm');
/**
 * Encrypt with ECIES: ephemeral X25519 key agreement + HKDF + AES-256-GCM.
 */
export async function encryptAsymmetric(plaintext, recipientPublicKey) {
    if (recipientPublicKey.length !== KEY_LENGTH) {
        throw new Error(`Invalid public key length: expected ${KEY_LENGTH}, got ${recipientPublicKey.length}`);
    }
    const ephemeralPrivate = generateRandomBytes(32);
    const ephemeralPublicKey = getX25519PublicKey(ephemeralPrivate);
    const sharedSecret = x25519SharedSecret(ephemeralPrivate, recipientPublicKey);
    const encryptionKeyBytes = hkdf(sha256, sharedSecret, undefined, ECIES_INFO, 32);
    const cryptoKey = await crypto.subtle.importKey('raw', toBuffer(encryptionKeyBytes), { name: 'AES-GCM' }, false, ['encrypt']);
    const nonce = generateRandomBytes(NONCE_LENGTH_AES);
    const ciphertext = await crypto.subtle.encrypt({ name: 'AES-GCM', iv: toBuffer(nonce) }, cryptoKey, toBuffer(plaintext));
    clearBytes(ephemeralPrivate);
    clearBytes(sharedSecret);
    clearBytes(encryptionKeyBytes);
    return {
        ephemeralPublicKey,
        ciphertext: new Uint8Array(ciphertext),
        nonce,
    };
}
/**
 * Decrypt with ECIES: X25519 key agreement + HKDF + AES-256-GCM.
 */
export async function decryptAsymmetric(encrypted, privateKey) {
    if (privateKey.length !== KEY_LENGTH) {
        throw new Error(`Invalid private key length: expected ${KEY_LENGTH}, got ${privateKey.length}`);
    }
    if (encrypted.nonce.length !== NONCE_LENGTH_AES) {
        throw new Error(`Invalid nonce length: expected ${NONCE_LENGTH_AES}, got ${encrypted.nonce.length}`);
    }
    const sharedSecret = x25519SharedSecret(privateKey, encrypted.ephemeralPublicKey);
    const encryptionKeyBytes = hkdf(sha256, sharedSecret, undefined, ECIES_INFO, 32);
    const cryptoKey = await crypto.subtle.importKey('raw', toBuffer(encryptionKeyBytes), { name: 'AES-GCM' }, false, ['decrypt']);
    const plaintext = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: toBuffer(encrypted.nonce) }, cryptoKey, toBuffer(encrypted.ciphertext));
    clearBytes(sharedSecret);
    clearBytes(encryptionKeyBytes);
    return new Uint8Array(plaintext);
}
//# sourceMappingURL=ecies.js.map