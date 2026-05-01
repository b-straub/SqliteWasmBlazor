import { describe, it, expect } from 'vitest';
import { encryptAesGcm, decryptAesGcm, generateRandomBytes } from '../src/crypto-core/index.js';

describe('aesGcm', () => {
    it('encrypt/decrypt round-trip', async () => {
        const key = generateRandomBytes(32);
        const plaintext = new TextEncoder().encode('secret data');

        const encrypted = await encryptAesGcm(plaintext, key);
        expect(encrypted.nonce.length).toBe(12);
        expect(encrypted.ciphertext.length).toBeGreaterThan(0);

        const decrypted = await decryptAesGcm(encrypted, key);
        expect(new TextDecoder().decode(decrypted)).toBe('secret data');
    });

    it('AAD binding works', async () => {
        const key = generateRandomBytes(32);
        const plaintext = new TextEncoder().encode('data');
        const aad = new TextEncoder().encode('context-v1');

        const encrypted = await encryptAesGcm(plaintext, key, aad);
        const decrypted = await decryptAesGcm(encrypted, key, aad);
        expect(new TextDecoder().decode(decrypted)).toBe('data');
    });

    it('wrong AAD fails', async () => {
        const key = generateRandomBytes(32);
        const plaintext = new TextEncoder().encode('data');
        const aad = new TextEncoder().encode('context-v1');
        const wrongAad = new TextEncoder().encode('context-v2');

        const encrypted = await encryptAesGcm(plaintext, key, aad);
        await expect(decryptAesGcm(encrypted, key, wrongAad)).rejects.toThrow();
    });

    it('wrong key fails', async () => {
        const key1 = generateRandomBytes(32);
        const key2 = generateRandomBytes(32);
        const plaintext = new TextEncoder().encode('data');

        const encrypted = await encryptAesGcm(plaintext, key1);
        await expect(decryptAesGcm(encrypted, key2)).rejects.toThrow();
    });

    it('rejects invalid key length', async () => {
        const shortKey = generateRandomBytes(16);
        const plaintext = new TextEncoder().encode('data');
        await expect(encryptAesGcm(plaintext, shortKey)).rejects.toThrow(/Invalid key length/);
    });
});
