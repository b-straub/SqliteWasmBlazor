import { describe, it, expect } from 'vitest';
import { encryptChaCha20Poly1305, decryptChaCha20Poly1305, generateRandomBytes } from '../src/crypto-core/index.js';
import type { SymmetricEncryptedData } from '../src/crypto-core/index.js';

describe('chacha20Poly1305', () => {
    it('RFC 8439 §2.8.2 test vector', () => {
        const key = new Uint8Array([
            0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8a, 0x8b, 0x8c, 0x8d, 0x8e, 0x8f,
            0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97,
            0x98, 0x99, 0x9a, 0x9b, 0x9c, 0x9d, 0x9e, 0x9f,
        ]);
        const nonce = new Uint8Array([
            0x07, 0x00, 0x00, 0x00, 0x40, 0x41, 0x42, 0x43,
            0x44, 0x45, 0x46, 0x47,
        ]);
        const aad = new Uint8Array([
            0x50, 0x51, 0x52, 0x53, 0xc0, 0xc1, 0xc2, 0xc3,
            0xc4, 0xc5, 0xc6, 0xc7,
        ]);
        const plaintext = new TextEncoder().encode(
            "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it."
        );

        // Expected ciphertext + tag from RFC 8439 §2.8.2
        const expectedCiphertextAndTag = new Uint8Array([
            0xd3, 0x1a, 0x8d, 0x34, 0x64, 0x8e, 0x60, 0xdb,
            0x7b, 0x86, 0xaf, 0xbc, 0x53, 0xef, 0x7e, 0xc2,
            0xa4, 0xad, 0xed, 0x51, 0x29, 0x6e, 0x08, 0xfe,
            0xa9, 0xe2, 0xb5, 0xa7, 0x36, 0xee, 0x62, 0xd6,
            0x3d, 0xbe, 0xa4, 0x5e, 0x8c, 0xa9, 0x67, 0x12,
            0x82, 0xfa, 0xfb, 0x69, 0xda, 0x92, 0x72, 0x8b,
            0x1a, 0x71, 0xde, 0x0a, 0x9e, 0x06, 0x0b, 0x29,
            0x05, 0xd6, 0xa5, 0xb6, 0x7e, 0xcd, 0x3b, 0x36,
            0x92, 0xdd, 0xbd, 0x7f, 0x2d, 0x77, 0x8b, 0x8c,
            0x98, 0x03, 0xae, 0xe3, 0x28, 0x09, 0x1b, 0x58,
            0xfa, 0xb3, 0x24, 0xe4, 0xfa, 0xd6, 0x75, 0x94,
            0x55, 0x85, 0x80, 0x8b, 0x48, 0x31, 0xd7, 0xbc,
            0x3f, 0xf4, 0xde, 0xf0, 0x8e, 0x4b, 0x7a, 0x9d,
            0xe5, 0x76, 0xd2, 0x65, 0x86, 0xce, 0xc6, 0x4b,
            0x61, 0x16,
            // Poly1305 tag (16 bytes)
            0x1a, 0xe1, 0x0b, 0x59, 0x4f, 0x09, 0xe2, 0x6a,
            0x7e, 0x90, 0x2e, 0xcb, 0xd0, 0x60, 0x06, 0x91,
        ]);

        const encrypted: SymmetricEncryptedData = { ciphertext: expectedCiphertextAndTag, nonce };
        const decrypted = decryptChaCha20Poly1305(encrypted, key, aad);
        expect(decrypted).toEqual(plaintext);
    });

    it('encrypt/decrypt round-trip', () => {
        const key = generateRandomBytes(32);
        const plaintext = new TextEncoder().encode('secret data for ChaCha20-Poly1305');

        const encrypted = encryptChaCha20Poly1305(plaintext, key);
        expect(encrypted.nonce.length).toBe(12);
        expect(encrypted.ciphertext.length).toBeGreaterThan(0);

        const decrypted = decryptChaCha20Poly1305(encrypted, key);
        expect(new TextDecoder().decode(decrypted)).toBe('secret data for ChaCha20-Poly1305');
    });

    it('AAD mismatch fails', () => {
        const key = generateRandomBytes(32);
        const plaintext = new TextEncoder().encode('data');
        const aad = new TextEncoder().encode('context-v1');
        const wrongAad = new TextEncoder().encode('context-v2');

        const encrypted = encryptChaCha20Poly1305(plaintext, key, aad);
        expect(() => decryptChaCha20Poly1305(encrypted, key, wrongAad)).toThrow();
    });

    it('wrong key fails', () => {
        const key1 = generateRandomBytes(32);
        const key2 = generateRandomBytes(32);
        const plaintext = new TextEncoder().encode('data');

        const encrypted = encryptChaCha20Poly1305(plaintext, key1);
        expect(() => decryptChaCha20Poly1305(encrypted, key2)).toThrow();
    });

    it('nonce uniqueness — same plaintext produces different ciphertexts', () => {
        const key = generateRandomBytes(32);
        const plaintext = new TextEncoder().encode('same data');

        const encrypted1 = encryptChaCha20Poly1305(plaintext, key);
        const encrypted2 = encryptChaCha20Poly1305(plaintext, key);

        expect(encrypted1.nonce).not.toEqual(encrypted2.nonce);
        expect(encrypted1.ciphertext).not.toEqual(encrypted2.ciphertext);
    });

    it('rejects invalid key length', () => {
        const shortKey = generateRandomBytes(16);
        const plaintext = new TextEncoder().encode('data');
        expect(() => encryptChaCha20Poly1305(plaintext, shortKey)).toThrow(/Invalid key length/);
    });
});
