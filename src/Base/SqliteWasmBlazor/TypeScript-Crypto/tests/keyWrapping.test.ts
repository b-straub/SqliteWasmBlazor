import { describe, it, expect } from 'vitest';
import { generateContentKey, wrapContentKey, unwrapContentKey, generateRandomBytes } from '../src/crypto-core/index.js';

describe('keyWrapping', () => {
    it('wrap/unwrap round-trip preserves CEK', async () => {
        const cek = generateContentKey();
        const wrappingKey = generateRandomBytes(32);

        const wrapped = await wrapContentKey(cek, wrappingKey);
        expect(wrapped.ciphertext.length).toBeGreaterThan(0);
        expect(wrapped.nonce.length).toBe(12);

        const unwrapped = await unwrapContentKey(wrapped, wrappingKey);
        expect(unwrapped).toEqual(cek);
    });

    it('wrong wrapping key fails', async () => {
        const cek = generateContentKey();
        const wrappingKey = generateRandomBytes(32);
        const wrongKey = generateRandomBytes(32);

        const wrapped = await wrapContentKey(cek, wrappingKey);
        await expect(unwrapContentKey(wrapped, wrongKey)).rejects.toThrow();
    });

    it('generateContentKey produces 32 bytes', () => {
        const cek = generateContentKey();
        expect(cek.length).toBe(32);
    });
});
