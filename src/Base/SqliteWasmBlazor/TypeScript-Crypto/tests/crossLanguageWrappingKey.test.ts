import { describe, it, expect } from 'vitest';
import {
    deriveDualKeyPair,
    deriveWrappingKey,
    base64ToBytes,
    bytesToBase64,
} from '../src/crypto-core/index.js';

// Cross-language byte-equality vectors for `deriveWrappingKey`
// (X25519 ECDH + HKDF-SHA256 with caller-supplied context).
//
// Mirrored xUnit at
// `tests/SqliteWasmBlazor.CryptoSync.Tests/CrossLanguageWrappingKeyVectorTests.cs`.
// The wrapping key binds CEKs across devices in the multi-recipient
// sharing primitive — both sides MUST agree byte-for-byte.

interface Vector {
    label: string;
    ownerSeed: Uint8Array;
    recipientSeed: Uint8Array;
    context: string;
    wrappingKey: string;
}

const ZEROS = new Uint8Array(32);
const ONES = new Uint8Array(32).fill(0xff);
const SEQUENTIAL = Uint8Array.from(Array.from({ length: 32 }, (_, i) => i));

const VECTORS: Vector[] = [
    {
        label: 'zeros->ones, group:v1',
        ownerSeed: ZEROS,
        recipientSeed: ONES,
        context: 'group:v1',
        wrappingKey: 'UghC2EP75/swpP5nt316lfSPZVJh5O9hfNyOPNfD8bQ=',
    },
    {
        label: 'ones->sequential, ecies-aes-gcm',
        ownerSeed: ONES,
        recipientSeed: SEQUENTIAL,
        context: 'ecies-aes-gcm',
        wrappingKey: '8dIqYwsHM2yASQujK/cyxUEbip53FzT9sjs0x/vow3k=',
    },
    {
        label: 'sequential->zeros, empty context',
        ownerSeed: SEQUENTIAL,
        recipientSeed: ZEROS,
        context: '',
        wrappingKey: '+ZpodXMedmT/qFovITlJIGExWDE7zZo+xiJO0OJkhyU=',
    },
];

describe('cross-language wrapping-key vectors', () => {
    for (const v of VECTORS) {
        it(`deriveWrappingKey matches BouncyCastle vector — ${v.label}`, async () => {
            const owner = await deriveDualKeyPair(v.ownerSeed);
            const recipient = await deriveDualKeyPair(v.recipientSeed);

            const actual = await deriveWrappingKey(
                owner.x25519PrivateKey,
                recipient.x25519PublicKey,
                v.context
            );

            expect(actual.length).toBe(32);
            expect(bytesToBase64(actual)).toBe(v.wrappingKey);
        });
    }

    it('deriveWrappingKey is deterministic for the same context', async () => {
        const owner = await deriveDualKeyPair(ZEROS);
        const recipient = await deriveDualKeyPair(ONES);

        const first = await deriveWrappingKey(owner.x25519PrivateKey, recipient.x25519PublicKey, 'group:v1');
        const second = await deriveWrappingKey(owner.x25519PrivateKey, recipient.x25519PublicKey, 'group:v1');

        expect(bytesToBase64(first)).toBe(bytesToBase64(second));
    });

    it('different context yields different wrapping key', async () => {
        const owner = await deriveDualKeyPair(ZEROS);
        const recipient = await deriveDualKeyPair(ONES);

        const ctxA = await deriveWrappingKey(owner.x25519PrivateKey, recipient.x25519PublicKey, 'ctx-a');
        const ctxB = await deriveWrappingKey(owner.x25519PrivateKey, recipient.x25519PublicKey, 'ctx-b');

        expect(bytesToBase64(ctxA)).not.toBe(bytesToBase64(ctxB));
    });

    it('decoding the C# golden bytes-to-Uint8Array round-trips', () => {
        // Smoke test that the base64 helpers used to mirror C#-emitted goldens
        // are compatible with Uint8Array semantics on this platform.
        for (const v of VECTORS) {
            const decoded = base64ToBytes(v.wrappingKey);
            expect(decoded.length).toBe(32);
            expect(bytesToBase64(decoded)).toBe(v.wrappingKey);
        }
    });
});
