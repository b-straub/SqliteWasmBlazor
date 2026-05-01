import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import {
    storeKeys,
    signWithCachedKey,
    encryptSymmetricCachedAesGcm,
    decryptSymmetricCachedAesGcm,
    decryptAsymmetricCachedAesGcm,
    getPublicKeys,
    hasKey,
    removeKeys,
    clearAllKeys,
} from '../src/crypto.js';
import {
    ed25519Verify,
    encryptAsymmetric,
    bytesToBase64,
} from '../packages/crypto-core/src/index.js';

// R1.5 — round-trip tests for the JS-side crypto bridge that NobleCryptoProvider
// dispatches to from C# via NobleInterop. The bridge in `src/crypto.ts` is the
// canonical message handler: it owns the keyId cache (non-extractable
// SubtleCrypto keys for Ed25519 / AES-GCM, raw bytes for X25519) and exposes
// the entry points the C# side calls — `storeKeys`, `signWithCachedKey`,
// `encryptSymmetricCachedAesGcm`, `decryptSymmetricCachedAesGcm`,
// `decryptAsymmetricCachedAesGcm`, `removeKeys`.
//
// All seeds are deterministic so the test surface matches what an xUnit
// harness would assert via Playwright on a real authenticator.

const KEY_ID = 'test:r1.5';
const SEED = Uint8Array.from(Array.from({ length: 32 }, (_, i) => i + 1));

describe('crypto bridge dispatch (R1.5)', () => {
    beforeEach(() => {
        clearAllKeys();
    });

    afterEach(() => {
        clearAllKeys();
    });

    it('storeKeys returns concatenated [x25519Pub | ed25519Pub] = 64 bytes', async () => {
        const publicKeys = await storeKeys(KEY_ID, SEED, null);

        expect(publicKeys.length).toBe(64);
        expect(hasKey(KEY_ID)).toBe(true);

        const reread = getPublicKeys(KEY_ID);
        expect(bytesToBase64(reread)).toBe(bytesToBase64(publicKeys));
    });

    it('signWithCachedKey produces a verifiable Ed25519 signature', async () => {
        await storeKeys(KEY_ID, SEED, null);
        const publicKeys = getPublicKeys(KEY_ID);
        const ed25519Pub = publicKeys.slice(32, 64);
        const message = new TextEncoder().encode('hello-r1.5');

        const sig = await signWithCachedKey(KEY_ID, message);

        expect(sig.length).toBe(64);
        expect(ed25519Verify(sig, message, ed25519Pub)).toBe(true);
    });

    it('signWithCachedKey is deterministic for the same (cached-key, message) pair', async () => {
        await storeKeys(KEY_ID, SEED, null);
        const message = new TextEncoder().encode('repeatable');

        const sigA = await signWithCachedKey(KEY_ID, message);
        const sigB = await signWithCachedKey(KEY_ID, message);

        expect(bytesToBase64(sigA)).toBe(bytesToBase64(sigB));
    });

    it('encryptSymmetricCachedAesGcm + decryptSymmetricCachedAesGcm round-trip', async () => {
        await storeKeys(KEY_ID, SEED, null);
        const plaintext = new TextEncoder().encode('symmetric-payload');

        const packed = await encryptSymmetricCachedAesGcm(KEY_ID, plaintext, null);
        // Layout: [nonce(12) | ciphertext]
        expect(packed.length).toBeGreaterThan(12);
        const nonce = packed.slice(0, 12);
        const ciphertext = packed.slice(12);

        const recovered = await decryptSymmetricCachedAesGcm(KEY_ID, ciphertext, nonce, null);
        expect(new TextDecoder().decode(recovered)).toBe('symmetric-payload');
    });

    it('encryptSymmetricCachedAesGcm honors AAD binding', async () => {
        await storeKeys(KEY_ID, SEED, null);
        const plaintext = new TextEncoder().encode('aad-bound');

        const packed = await encryptSymmetricCachedAesGcm(KEY_ID, plaintext, 'group-a:v1');
        const nonce = packed.slice(0, 12);
        const ciphertext = packed.slice(12);

        // Wrong AAD must fail
        const wrong = await decryptSymmetricCachedAesGcm(KEY_ID, ciphertext, nonce, 'group-b:v1');
        expect(wrong.length).toBe(0);

        // Correct AAD round-trips
        const right = await decryptSymmetricCachedAesGcm(KEY_ID, ciphertext, nonce, 'group-a:v1');
        expect(new TextDecoder().decode(right)).toBe('aad-bound');
    });

    it('decryptAsymmetricCachedAesGcm round-trips with crypto-core encryptAsymmetric', async () => {
        await storeKeys(KEY_ID, SEED, null);
        const publicKeys = getPublicKeys(KEY_ID);
        const x25519Pub = publicKeys.slice(0, 32);
        const plaintext = new TextEncoder().encode('asymmetric-payload');

        const encrypted = await encryptAsymmetric(plaintext, x25519Pub);
        const recovered = await decryptAsymmetricCachedAesGcm(
            KEY_ID,
            encrypted.ephemeralPublicKey,
            encrypted.ciphertext,
            encrypted.nonce
        );

        expect(new TextDecoder().decode(recovered)).toBe('asymmetric-payload');
    });

    it('removeKeys evicts the cache so subsequent ops return empty Uint8Array', async () => {
        await storeKeys(KEY_ID, SEED, null);
        expect(hasKey(KEY_ID)).toBe(true);

        removeKeys(KEY_ID);
        expect(hasKey(KEY_ID)).toBe(false);

        const sig = await signWithCachedKey(KEY_ID, new TextEncoder().encode('after-evict'));
        expect(sig.length).toBe(0);

        const sym = await encryptSymmetricCachedAesGcm(KEY_ID, new TextEncoder().encode('p'), null);
        expect(sym.length).toBe(0);
    });

    it('signWithCachedKey on missing keyId returns empty Uint8Array (no throw)', async () => {
        const sig = await signWithCachedKey('never-stored', new TextEncoder().encode('x'));
        expect(sig.length).toBe(0);
    });
});
