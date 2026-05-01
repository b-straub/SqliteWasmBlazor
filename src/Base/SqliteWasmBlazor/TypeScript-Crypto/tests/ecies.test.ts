import { describe, it, expect } from 'vitest';
import { encryptAsymmetric, decryptAsymmetric, generateX25519KeyPair } from '../src/crypto-core/index.js';

describe('ecies', () => {
    it('encrypt/decrypt round-trip', async () => {
        const recipient = generateX25519KeyPair();
        const plaintext = new TextEncoder().encode('asymmetric secret');

        const encrypted = await encryptAsymmetric(plaintext, recipient.publicKey);
        expect(encrypted.ephemeralPublicKey.length).toBe(32);
        expect(encrypted.nonce.length).toBe(12);

        const decrypted = await decryptAsymmetric(encrypted, recipient.privateKey);
        expect(new TextDecoder().decode(decrypted)).toBe('asymmetric secret');
    });

    it('wrong private key fails', async () => {
        const recipient = generateX25519KeyPair();
        const wrongKey = generateX25519KeyPair();
        const plaintext = new TextEncoder().encode('data');

        const encrypted = await encryptAsymmetric(plaintext, recipient.publicKey);
        await expect(decryptAsymmetric(encrypted, wrongKey.privateKey)).rejects.toThrow();
    });
});
