import { describe, it, expect } from 'vitest';
import {
    base64ToBytes,
    bytesToBase64,
    decryptAesGcm,
    toBuffer,
} from '../src/crypto-core/index.js';
import type { SymmetricEncryptedData } from '../src/crypto-core/index.js';

// Cross-language byte-equality vectors for AES-256-GCM at the primitive level —
// the engine underneath `wrapContentKey` / `unwrapContentKey` and ad-hoc
// symmetric encryption flows.
//
// Mirrored xUnit at
// `tests/SqliteWasmBlazor.CryptoSync.Tests/CrossLanguageAesGcmVectorTests.cs`.
//
// Production wrappers (`wrapContentKey`, `WrapContentKeyAsync`) generate a
// random nonce internally, so the vector here drives `crypto.subtle.encrypt`
// directly with a fixed nonce on the TS side. Both sides must produce the
// same ciphertext+tag bytes for the wire format to round-trip safely.

interface Vector {
    label: string;
    key: Uint8Array;
    nonce: Uint8Array;
    plaintext: Uint8Array;
    aad: Uint8Array | null;
    ctWithTag: string; // base64
}

const KEY_ZEROS = new Uint8Array(32);
const KEY_ONES = new Uint8Array(32).fill(0xff);
const KEY_SEQUENTIAL = Uint8Array.from(Array.from({ length: 32 }, (_, i) => i));

const NONCE_COUNTING = Uint8Array.from(Array.from({ length: 12 }, (_, i) => i));
const NONCE_MAXED = new Uint8Array(12).fill(0xab);

const PT_HELLO = new Uint8Array([
    0x48, 0x65, 0x6c, 0x6c, 0x6f, 0x2c, 0x20, 0x57,
    0x6f, 0x72, 0x6c, 0x64, 0x21, 0x00, 0x00, 0x00,
]);
const PT_EMPTY = new Uint8Array(0);
const PT_LONG = Uint8Array.from(Array.from({ length: 64 }, (_, i) => (i * 3) & 0xff));

const VECTORS: Vector[] = [
    {
        label: 'zeros-key, counting-nonce, hello-plain',
        key: KEY_ZEROS,
        nonce: NONCE_COUNTING,
        plaintext: PT_HELLO,
        aad: null,
        ctWithTag: 'wK5fPm/Rr66tCj6B3APeDNjS1n1eJyqwmVIV94GO/v0=',
    },
    {
        label: 'ones-key, maxed-nonce, empty-plain',
        key: KEY_ONES,
        nonce: NONCE_MAXED,
        plaintext: PT_EMPTY,
        aad: null,
        ctWithTag: 'sGQSwNi8mT7Ke7mwUpuoNQ==',
    },
    {
        label: 'sequential-key, counting-nonce, long-plain, with-aad',
        key: KEY_SEQUENTIAL,
        nonce: NONCE_COUNTING,
        plaintext: PT_LONG,
        aad: new TextEncoder().encode('header:v1'),
        ctWithTag: 'RwHQEsnq0A6VWomqlc5SQLPlsQ3MRB05cCyr1Ek+Wu9hc8iVw65g7QzfAWwMAKK1fsr2FMZJAX+XPISorFRPU1p31QVCyT+5013TBQHhuCU=',
    },
];

async function encryptFixedNonce(
    plaintext: Uint8Array,
    key: Uint8Array,
    nonce: Uint8Array,
    aad: Uint8Array | null
): Promise<Uint8Array> {
    const cryptoKey = await crypto.subtle.importKey(
        'raw', toBuffer(key), { name: 'AES-GCM' }, false, ['encrypt']
    );
    const params: AesGcmParams = { name: 'AES-GCM', iv: toBuffer(nonce) };
    if (aad !== null) {
        params.additionalData = toBuffer(aad);
    }
    const ct = await crypto.subtle.encrypt(params, cryptoKey, toBuffer(plaintext));
    return new Uint8Array(ct);
}

describe('cross-language AES-256-GCM vectors', () => {
    for (const v of VECTORS) {
        it(`encrypt matches BouncyCastle vector — ${v.label}`, async () => {
            const ct = await encryptFixedNonce(v.plaintext, v.key, v.nonce, v.aad);
            expect(bytesToBase64(ct)).toBe(v.ctWithTag);
        });

        it(`decryptAesGcm round-trips BouncyCastle ciphertext — ${v.label}`, async () => {
            const wrapped: SymmetricEncryptedData = {
                ciphertext: base64ToBytes(v.ctWithTag),
                nonce: v.nonce,
            };
            const aad = v.aad ?? undefined;
            const pt = await decryptAesGcm(wrapped, v.key, aad);
            expect(bytesToBase64(pt)).toBe(bytesToBase64(v.plaintext));
        });
    }

    it('decrypt fails on tag mismatch', async () => {
        const ct = base64ToBytes('wK5fPm/Rr66tCj6B3APeDNjS1n1eJyqwmVIV94GO/v0=');
        ct[ct.length - 1] ^= 0x01;

        const wrapped: SymmetricEncryptedData = {
            ciphertext: ct,
            nonce: NONCE_COUNTING,
        };

        await expect(decryptAesGcm(wrapped, KEY_ZEROS)).rejects.toThrow();
    });

    it('aad changes the tag', async () => {
        const without = await encryptFixedNonce(PT_HELLO, KEY_ZEROS, NONCE_COUNTING, null);
        const withAad = await encryptFixedNonce(
            PT_HELLO, KEY_ZEROS, NONCE_COUNTING, new TextEncoder().encode('header:v1'));

        expect(bytesToBase64(without)).not.toBe(bytesToBase64(withAad));
    });
});
