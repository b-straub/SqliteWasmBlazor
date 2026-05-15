import { describe, it, expect } from 'vitest';
import { base64ToBytes, bytesToBase64, base64UrlToBase64, clearBytes, concatBytes, generateRandomBytes } from '../src/crypto-core/index.js';

describe('utils', () => {
    it('base64 round-trip', () => {
        const original = new Uint8Array([1, 2, 3, 4, 5, 255, 0, 128]);
        const b64 = bytesToBase64(original);
        const decoded = base64ToBytes(b64);
        expect(decoded).toEqual(original);
    });

    it('base64UrlToBase64 converts correctly', () => {
        expect(base64UrlToBase64('abc-def_ghi')).toBe('abc+def/ghi=');
    });

    it('clearBytes zeroes memory', () => {
        const buf = new Uint8Array([1, 2, 3, 4]);
        clearBytes(buf);
        expect(buf).toEqual(new Uint8Array(4));
    });

    it('concatBytes joins arrays', () => {
        const a = new Uint8Array([1, 2]);
        const b = new Uint8Array([3, 4, 5]);
        expect(concatBytes(a, b)).toEqual(new Uint8Array([1, 2, 3, 4, 5]));
    });

    it('generateRandomBytes returns correct length', () => {
        const bytes = generateRandomBytes(32);
        expect(bytes.length).toBe(32);
        // Should not be all zeros (astronomically unlikely)
        expect(bytes.some(b => b !== 0)).toBe(true);
    });
});
