import { describe, it, expect } from 'vitest';
import { generateEd25519KeyPair, getEd25519PublicKey, ed25519Sign, ed25519Verify } from '../src/index.js';

describe('ed25519', () => {
    it('generates 32-byte key pair', () => {
        const kp = generateEd25519KeyPair();
        expect(kp.privateKey.length).toBe(32);
        expect(kp.publicKey.length).toBe(32);
    });

    it('derives public key deterministically', () => {
        const kp = generateEd25519KeyPair();
        const pub = getEd25519PublicKey(kp.privateKey);
        expect(pub).toEqual(kp.publicKey);
    });

    it('sign/verify round-trip', () => {
        const kp = generateEd25519KeyPair();
        const message = new TextEncoder().encode('hello world');

        const signature = ed25519Sign(message, kp.privateKey);
        expect(signature.length).toBe(64);

        expect(ed25519Verify(signature, message, kp.publicKey)).toBe(true);
    });

    it('rejects tampered message', () => {
        const kp = generateEd25519KeyPair();
        const message = new TextEncoder().encode('hello world');
        const signature = ed25519Sign(message, kp.privateKey);

        const tampered = new TextEncoder().encode('hello worl!');
        expect(ed25519Verify(signature, tampered, kp.publicKey)).toBe(false);
    });

    it('rejects wrong public key', () => {
        const kp1 = generateEd25519KeyPair();
        const kp2 = generateEd25519KeyPair();
        const message = new TextEncoder().encode('test');
        const signature = ed25519Sign(message, kp1.privateKey);

        expect(ed25519Verify(signature, message, kp2.publicKey)).toBe(false);
    });
});
