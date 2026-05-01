import { describe, it, expect } from 'vitest';
import {
    base64ToBytes,
    bytesToBase64,
    decryptAsymmetric,
    deriveDualKeyPair,
    deriveWrappingKey,
    getX25519PublicKey,
    toBuffer,
} from '../src/crypto-core/index.js';

// Cross-language byte-equality vectors for the ECIES combinator (X25519 ECDH +
// HKDF-SHA256 with info="ecies-aes-gcm" + AES-256-GCM).
//
// Mirrored xUnit at
// `tests/SqliteWasmBlazor.CryptoSync.Tests/CrossLanguageEciesVectorTests.cs`.
//
// Production wrappers — `encryptAsymmetric` (TS) and `EncryptAsymmetricAsync`
// (C#) — generate a random ephemeral keypair and a random nonce, so they
// aren't directly vector-testable. The interop invariant is the *combinator*:
//
//   sharedSecret = X25519(ephPriv, recipientPub)
//   wrappingKey  = HKDF-SHA256(sharedSecret, salt=zeros[32], info="ecies-aes-gcm", 32)
//   ciphertext   = AES-256-GCM(plaintext, wrappingKey, nonce)
//
// The two vectors below pin (ephPriv, recipientPriv, nonce, plaintext) →
// (ephPub, ciphertext+tag). The production decrypt path then round-trips the
// captured wire bytes back to the original plaintext.

const ECIES_CONTEXT = 'ecies-aes-gcm';

interface Vector {
    label: string;
    ephSeed: Uint8Array;
    recipientSeed: Uint8Array;
    nonce: Uint8Array;
    plaintext: string;
    ephPub: string; // base64
    ctWithTag: string; // base64
}

const ZEROS = new Uint8Array(32);
const ONES = new Uint8Array(32).fill(0xff);
const SEQUENTIAL = Uint8Array.from(Array.from({ length: 32 }, (_, i) => i));

const NONCE_COUNTING = Uint8Array.from(Array.from({ length: 12 }, (_, i) => i));
const NONCE_MAXED = new Uint8Array(12).fill(0xab);

const VECTORS: Vector[] = [
    {
        label: 'zeros-eph -> ones-recipient, counting-nonce, hello',
        ephSeed: ZEROS,
        recipientSeed: ONES,
        nonce: NONCE_COUNTING,
        plaintext: 'Hello, ECIES!',
        ephPub: 'uB1Tmve6CWhkpqTqviODol9a+swrW48ctr+q2FEOrk8=',
        ctWithTag: 'WqWSoNNKDJQdErmofy0MW5boYQ4Bg9fCIXgxJJc=',
    },
    {
        label: 'sequential-eph -> ones-recipient, maxed-nonce, long',
        ephSeed: SEQUENTIAL,
        recipientSeed: ONES,
        nonce: NONCE_MAXED,
        plaintext: 'Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do.',
        ephPub: 'TNo8xxNR1wr2kEcDUpHPPGFY0ejFcFnMz38vVepD/XA=',
        ctWithTag: 'T1707zgxhLdiEjFydlgg+ddrM72zm9CEoPJ3uMFVCoshPAd6B4wKZKTRbg3s0X79lWeRz0hS62K3gqAEjur/8yBhuf0AAyLZZ3+na7hHuho=',
    },
];

async function composeEcies(v: Vector): Promise<{ ephPub: Uint8Array; ciphertext: Uint8Array }> {
    const ephemeral = deriveDualKeyPair(v.ephSeed);
    const recipient = deriveDualKeyPair(v.recipientSeed);

    const wrappingKey = deriveWrappingKey(
        ephemeral.x25519PrivateKey,
        recipient.x25519PublicKey,
        ECIES_CONTEXT
    );

    const cryptoKey = await crypto.subtle.importKey(
        'raw', toBuffer(wrappingKey), { name: 'AES-GCM' }, false, ['encrypt']
    );

    const plaintextBytes = new TextEncoder().encode(v.plaintext);
    const ciphertext = new Uint8Array(
        await crypto.subtle.encrypt(
            { name: 'AES-GCM', iv: toBuffer(v.nonce) },
            cryptoKey,
            toBuffer(plaintextBytes)
        )
    );

    return {
        ephPub: ephemeral.x25519PublicKey,
        ciphertext,
    };
}

describe('cross-language ECIES vectors', () => {
    for (const v of VECTORS) {
        it(`combinator matches BouncyCastle vector — ${v.label}`, async () => {
            const { ephPub, ciphertext } = await composeEcies(v);

            expect(bytesToBase64(ephPub)).toBe(v.ephPub);
            expect(bytesToBase64(ciphertext)).toBe(v.ctWithTag);
        });

        it(`production decryptAsymmetric round-trips BouncyCastle ciphertext — ${v.label}`, async () => {
            const recipient = deriveDualKeyPair(v.recipientSeed);

            const plaintext = await decryptAsymmetric(
                {
                    ephemeralPublicKey: base64ToBytes(v.ephPub),
                    ciphertext: base64ToBytes(v.ctWithTag),
                    nonce: v.nonce,
                },
                recipient.x25519PrivateKey
            );

            expect(new TextDecoder().decode(plaintext)).toBe(v.plaintext);
        });
    }

    it('ephemeral public key is the deterministic X25519 derivation of the seed', () => {
        // Sanity-check the combinator's anchor: ephPub is x25519.getPublicKey(ephPriv).
        for (const v of VECTORS) {
            const ephemeral = deriveDualKeyPair(v.ephSeed);
            const direct = getX25519PublicKey(ephemeral.x25519PrivateKey);

            expect(bytesToBase64(direct)).toBe(v.ephPub);
            expect(bytesToBase64(ephemeral.x25519PublicKey)).toBe(v.ephPub);
        }
    });

    it('combinator is deterministic for the same inputs', async () => {
        const a = await composeEcies(VECTORS[0]);
        const b = await composeEcies(VECTORS[0]);

        expect(bytesToBase64(a.ephPub)).toBe(bytesToBase64(b.ephPub));
        expect(bytesToBase64(a.ciphertext)).toBe(bytesToBase64(b.ciphertext));
    });
});
