import { describe, it, expect } from 'vitest';
import { generateX25519KeyPair, getX25519PublicKey, x25519SharedSecret } from '../src/crypto-core/index.js';

describe('x25519', () => {
    it('generates 32-byte key pair', () => {
        const kp = generateX25519KeyPair();
        expect(kp.privateKey.length).toBe(32);
        expect(kp.publicKey.length).toBe(32);
    });

    it('derives public key deterministically', () => {
        const kp = generateX25519KeyPair();
        const pub = getX25519PublicKey(kp.privateKey);
        expect(pub).toEqual(kp.publicKey);
    });

    it('shared secret agreement (Alice/Bob)', () => {
        const alice = generateX25519KeyPair();
        const bob = generateX25519KeyPair();

        const secretAlice = x25519SharedSecret(alice.privateKey, bob.publicKey);
        const secretBob = x25519SharedSecret(bob.privateKey, alice.publicKey);

        expect(secretAlice).toEqual(secretBob);
        expect(secretAlice.length).toBe(32);
    });
});
