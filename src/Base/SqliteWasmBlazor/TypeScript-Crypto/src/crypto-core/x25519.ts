// @sqlitewasmblazor/crypto-core — X25519 key exchange (ECDH, SubtleCrypto)

import { clearBytes, generateRandomBytes, toBuffer, wrapSeedInPkcs8, base64UrlToBase64, base64ToBytes } from './utils.js';
import type { KeyPair } from './types.js';

async function importX25519PrivateKey(seed: Uint8Array, extractable: boolean): Promise<CryptoKey> {
    const pkcs8 = wrapSeedInPkcs8(seed, 'X25519');
    try {
        return await crypto.subtle.importKey('pkcs8', toBuffer(pkcs8), { name: 'X25519' }, extractable, ['deriveBits']);
    } finally {
        clearBytes(pkcs8);
    }
}

async function importX25519PublicKey(publicKey: Uint8Array): Promise<CryptoKey> {
    return await crypto.subtle.importKey('raw', toBuffer(publicKey), { name: 'X25519' }, false, []);
}

// SubtleCrypto can't extract a pub from a non-extractable X25519 priv, so
// seed→pub goes through extractable=true. Seed is already in our heap so
// no new exposure.
async function x25519PublicFromSeed(seed: Uint8Array): Promise<Uint8Array> {
    const key = await importX25519PrivateKey(seed, true);
    const jwk = await crypto.subtle.exportKey('jwk', key);
    return base64ToBytes(base64UrlToBase64(jwk.x!));
}

export async function generateX25519KeyPair(): Promise<KeyPair> {
    const privateKey = generateRandomBytes(32);
    const publicKey = await x25519PublicFromSeed(privateKey);
    return { privateKey, publicKey };
}

export async function getX25519PublicKey(privateKey: Uint8Array): Promise<Uint8Array> {
    return await x25519PublicFromSeed(privateKey);
}

export async function x25519SharedSecret(privateKey: Uint8Array, publicKey: Uint8Array): Promise<Uint8Array> {
    const priv = await importX25519PrivateKey(privateKey, false);
    const pub = await importX25519PublicKey(publicKey);
    const shared = await crypto.subtle.deriveBits({ name: 'X25519', public: pub }, priv, 256);
    return new Uint8Array(shared);
}
