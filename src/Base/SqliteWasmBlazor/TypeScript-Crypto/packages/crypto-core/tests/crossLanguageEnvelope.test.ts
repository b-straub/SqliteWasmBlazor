import { describe, it, expect } from 'vitest';
import { buildCanonicalEnvelope } from '../src/group.js';
import type { SymmetricEncryptedData } from '../src/types.js';

// Cross-language byte-equality vector for the canonical envelope.
//
// Both sides hash the raw ciphertext bytes (TS already carries
// `Uint8Array`; C# decodes the base64 string first) and produce the same
// canonical string for the same logical inputs. Cross-language
// sign/verify round-trips depend on this agreement.
//
// Mirrored xUnit at
// `tests/SqliteWasmBlazor.CryptoSync.Tests/CrossLanguageCanonicalEnvelopeTests.cs`
// — the golden value below is identical on both sides.

const GROUP_CONTEXT = 'group-test:v1';
const KEY_VERSION = 1;

// Sender Ed25519 public key — 32 bytes, all 0x42.
const SENDER_PUB = new Uint8Array(32).fill(0x42);

// Ciphertext + nonce — fixed bytes so the envelope's SHA-256 is deterministic.
// Ciphertext = "Hello, World!\0\0\0" (16 bytes).
const CIPHERTEXT = new Uint8Array([
    0x48, 0x65, 0x6c, 0x6c, 0x6f, 0x2c, 0x20, 0x57,
    0x6f, 0x72, 0x6c, 0x64, 0x21, 0x00, 0x00, 0x00,
]);
const NONCE = Uint8Array.from(Array.from({ length: 12 }, (_, i) => i));

// Unified golden envelope string — identical on the C# and TS sides.
const EXPECTED_CANONICAL_ENVELOPE =
    'group-test:v1|1|QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkI=|vBNtBKM0PskIjlUP4MprfsH8qPedWnz9gPHybmtEpAQ=';

describe('cross-language canonical envelope', () => {
    it('TS buildCanonicalEnvelope is deterministic for fixed inputs', () => {
        const encrypted: SymmetricEncryptedData = {
            ciphertext: CIPHERTEXT,
            nonce: NONCE,
        };

        const first = buildCanonicalEnvelope(GROUP_CONTEXT, KEY_VERSION, SENDER_PUB, encrypted);
        const second = buildCanonicalEnvelope(GROUP_CONTEXT, KEY_VERSION, SENDER_PUB, encrypted);

        expect(first).toBe(second);
    });

    it('TS buildCanonicalEnvelope matches its golden vector', () => {
        const encrypted: SymmetricEncryptedData = {
            ciphertext: CIPHERTEXT,
            nonce: NONCE,
        };

        const actual = buildCanonicalEnvelope(GROUP_CONTEXT, KEY_VERSION, SENDER_PUB, encrypted);

        expect(actual).toBe(EXPECTED_CANONICAL_ENVELOPE);
    });
});
