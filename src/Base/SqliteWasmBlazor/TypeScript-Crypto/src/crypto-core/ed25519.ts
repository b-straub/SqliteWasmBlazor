// @sqlitewasmblazor/crypto-core — Ed25519 signing/verification (SubtleCrypto)

import { clearBytes, generateRandomBytes, toBuffer, wrapSeedInPkcs8, base64UrlToBase64, base64ToBytes } from './utils.js';
import type { KeyPair } from './types.js';

async function importEd25519PrivateKey(seed: Uint8Array, extractable: boolean): Promise<CryptoKey> {
    const pkcs8 = wrapSeedInPkcs8(seed, 'Ed25519');
    try {
        return await crypto.subtle.importKey('pkcs8', toBuffer(pkcs8), { name: 'Ed25519' }, extractable, ['sign']);
    } finally {
        clearBytes(pkcs8);
    }
}

async function importEd25519PublicKey(publicKey: Uint8Array): Promise<CryptoKey> {
    return await crypto.subtle.importKey('raw', toBuffer(publicKey), { name: 'Ed25519' }, false, ['verify']);
}

async function ed25519PublicFromSeed(seed: Uint8Array): Promise<Uint8Array> {
    const key = await importEd25519PrivateKey(seed, true);
    const jwk = await crypto.subtle.exportKey('jwk', key);
    return base64ToBytes(base64UrlToBase64(jwk.x!));
}

export async function generateEd25519KeyPair(): Promise<KeyPair> {
    const privateKey = generateRandomBytes(32);
    const publicKey = await ed25519PublicFromSeed(privateKey);
    return { privateKey, publicKey };
}

export async function getEd25519PublicKey(privateKey: Uint8Array): Promise<Uint8Array> {
    return await ed25519PublicFromSeed(privateKey);
}

export async function ed25519Sign(message: Uint8Array, privateKey: Uint8Array): Promise<Uint8Array> {
    const key = await importEd25519PrivateKey(privateKey, false);
    const sig = await crypto.subtle.sign({ name: 'Ed25519' }, key, toBuffer(message));
    return new Uint8Array(sig);
}

export async function ed25519Verify(signature: Uint8Array, message: Uint8Array, publicKey: Uint8Array): Promise<boolean> {
    try {
        const key = await importEd25519PublicKey(publicKey);
        return await crypto.subtle.verify({ name: 'Ed25519' }, key, toBuffer(signature), toBuffer(message));
    } catch {
        return false;
    }
}
