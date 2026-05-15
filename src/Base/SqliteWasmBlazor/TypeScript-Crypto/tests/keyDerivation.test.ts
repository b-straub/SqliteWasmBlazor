import { describe, it, expect } from 'vitest';
import {
    deriveX25519KeyPairFromSeed, deriveEd25519KeyPairFromSeed,
    deriveDualKeyPair, deriveHkdfKey, deriveWrappingKey, generateRandomBytes
} from '../src/crypto-core/index.js';

describe('keyDerivation', () => {
    it('deterministic X25519 derivation from same seed', async () => {
        const seed = generateRandomBytes(32);
        const kp1 = await deriveX25519KeyPairFromSeed(seed);
        const kp2 = await deriveX25519KeyPairFromSeed(seed);
        expect(kp1.publicKey).toEqual(kp2.publicKey);
    });

    it('deterministic Ed25519 derivation from same seed', async () => {
        const seed = generateRandomBytes(32);
        const kp1 = await deriveEd25519KeyPairFromSeed(seed);
        const kp2 = await deriveEd25519KeyPairFromSeed(seed);
        expect(kp1.publicKey).toEqual(kp2.publicKey);
    });

    it('different domains produce different keys', () => {
        const seed = generateRandomBytes(32);
        const key1 = deriveHkdfKey(seed, 'domain-a');
        const key2 = deriveHkdfKey(seed, 'domain-b');
        expect(key1).not.toEqual(key2);
    });

    it('deriveDualKeyPair produces both pairs from one seed', async () => {
        const seed = generateRandomBytes(32);
        const dual = await deriveDualKeyPair(seed);

        expect(dual.x25519PrivateKey.length).toBe(32);
        expect(dual.x25519PublicKey.length).toBe(32);
        expect(dual.ed25519PrivateKey.length).toBe(32);
        expect(dual.ed25519PublicKey.length).toBe(32);

        // Keys should be different between X25519 and Ed25519
        expect(dual.x25519PrivateKey).not.toEqual(dual.ed25519PrivateKey);
    });

    it('wrapping key is consistent for same inputs', async () => {
        const seed = generateRandomBytes(32);
        const kpA = await deriveX25519KeyPairFromSeed(seed);
        const kpB = await deriveX25519KeyPairFromSeed(generateRandomBytes(32));

        const wk1 = await deriveWrappingKey(kpA.privateKey, kpB.publicKey, 'ctx');
        const wk2 = await deriveWrappingKey(kpA.privateKey, kpB.publicKey, 'ctx');
        expect(wk1).toEqual(wk2);
        expect(wk1.length).toBe(32);
    });
});
