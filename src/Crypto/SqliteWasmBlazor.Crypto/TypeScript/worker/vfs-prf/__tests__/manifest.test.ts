import { describe, it, expect } from 'vitest';
import {
    MANIFEST_LENGTH,
    deriveManifestMacKey,
    emptyManifestRegion,
    parseManifestRegion,
    serializeManifestRegion,
} from '../manifest.js';

const sampleGlobalKey = (() => {
    const k = new Uint8Array(32);
    for (let i = 0; i < 32; i++) k[i] = (i * 7 + 3) & 0xff;
    return k;
})();

const sampleBody = new Uint8Array([
    // pretend MessagePack { credentialId: bytes(8), pubkeyFingerprint: bytes(4) }
    0x82, 0xa1, 0x69, 0xc4, 0x08, 1, 2, 3, 4, 5, 6, 7, 8,
    0xa3, 0x66, 0x70, 0x72, 0xc4, 0x04, 9, 10, 11, 12,
]);

describe('disk-manifest layout', () => {
    it('round-trips body bytes through serialize → parse', () => {
        const macKey = deriveManifestMacKey(sampleGlobalKey);
        const region = serializeManifestRegion(sampleBody, macKey);
        expect(region.length).toBe(MANIFEST_LENGTH);

        const parsed = parseManifestRegion(region, macKey);
        expect(parsed.state).toBe('present');
        expect(parsed.schemaVersion).toBe(0x01);
        expect(parsed.body).toBeDefined();
        expect(Array.from(parsed.body!)).toEqual(Array.from(sampleBody));
    });

    it('reports absent for an all-zero region (cleared)', () => {
        const region = emptyManifestRegion();
        const parsed = parseManifestRegion(region);
        expect(parsed.state).toBe('absent');
        expect(parsed.body).toBeUndefined();
    });

    it('detects a tampered body byte via HMAC', () => {
        const macKey = deriveManifestMacKey(sampleGlobalKey);
        const region = serializeManifestRegion(sampleBody, macKey);

        // Flip one body byte (offset 8 = first byte of body).
        region[8] ^= 0x01;

        const parsed = parseManifestRegion(region, macKey);
        expect(parsed.state).toBe('tampered');
    });

    it('detects a tampered MAC byte', () => {
        const macKey = deriveManifestMacKey(sampleGlobalKey);
        const region = serializeManifestRegion(sampleBody, macKey);

        // MAC lives at region-relative offsets 468..499 (MANIFEST_LENGTH - 32).
        region[480] ^= 0x80;

        const parsed = parseManifestRegion(region, macKey);
        expect(parsed.state).toBe('tampered');
    });

    it('returns present without verifying when macKey is omitted (pre-unlock read)', () => {
        const macKey = deriveManifestMacKey(sampleGlobalKey);
        const region = serializeManifestRegion(sampleBody, macKey);

        // Pre-unlock callers cannot verify (no globalKey yet). They just want
        // the body bytes to surface credentialId for the auth-flow check.
        region[480] ^= 0xff; // tamper, but we're not verifying.

        const parsed = parseManifestRegion(region); // no macKey
        expect(parsed.state).toBe('present');
        expect(parsed.body).toBeDefined();
    });

    it('reports malformed when magic is corrupt but non-zero', () => {
        const macKey = deriveManifestMacKey(sampleGlobalKey);
        const region = serializeManifestRegion(sampleBody, macKey);
        region[0] = 0x42; // not "PFAM", not zero
        const parsed = parseManifestRegion(region, macKey);
        expect(parsed.state).toBe('malformed');
    });

    it('refuses bodies that exceed the available space', () => {
        const macKey = deriveManifestMacKey(sampleGlobalKey);
        const oversized = new Uint8Array(500); // > MAX_BODY_LEN (460)
        expect(() => serializeManifestRegion(oversized, macKey)).toThrow(/too large/);
    });

    it('different macKeys produce different MACs (rotation isolates)', () => {
        const altGlobalKey = new Uint8Array(32);
        for (let i = 0; i < 32; i++) altGlobalKey[i] = (i * 11 + 5) & 0xff;

        const k1 = deriveManifestMacKey(sampleGlobalKey);
        const k2 = deriveManifestMacKey(altGlobalKey);

        const r1 = serializeManifestRegion(sampleBody, k1);
        // r1 was MACed under k1. Verifying under k2 must fail.
        const parsed = parseManifestRegion(r1, k2);
        expect(parsed.state).toBe('tampered');
    });
});
