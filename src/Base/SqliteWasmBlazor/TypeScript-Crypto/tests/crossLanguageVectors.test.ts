import { describe, it, expect } from 'vitest';
import { deriveDualKeyPair, bytesToBase64 } from '../src/crypto-core/index.js';

// Cross-language byte-equality vectors for the dual-keypair derivation
// silently depended on by group encryption and cross-device sync.
//
// These golden values are mirrored in the C# xUnit test
// `tests/SqliteWasmBlazor.CryptoSync.Tests/CrossLanguageKdfVectorTests.cs`.
// Both sides MUST produce identical bytes for a given seed; this is what
// guarantees `BouncyCastleCryptoProvider` (server-side AdminSeed) and the
// browser noble path interoperate. If either side regresses, both sides
// must be updated together — and only after intentional protocol change.

interface Vector {
    label: string;
    seed: Uint8Array;
    x25519Priv: string;
    x25519Pub: string;
    ed25519Priv: string;
    ed25519Pub: string;
}

const VECTORS: Vector[] = [
    {
        label: 'zeros',
        seed: new Uint8Array(32),
        x25519Priv: '7123Lmo81uTvaGg6mF/JHqPhpk/g2YtPRXG5PtQiJuA=',
        x25519Pub: 'uB1Tmve6CWhkpqTqviODol9a+swrW48ctr+q2FEOrk8=',
        ed25519Priv: 'kfzKlKy3yQgFjDEHipNjhjIHzCmFbexUPp72od049pc=',
        ed25519Pub: 'lbQ0JurxU0mL7EibMqPsGb5R6VpAjJcyScMgUnN6nG0=',
    },
    {
        label: 'ones',
        seed: new Uint8Array(32).fill(0xff),
        x25519Priv: 'yNQ0da8rxEFYER3M903RWvMDPw2y8NIOBmkhqkP/ROE=',
        x25519Pub: 'u45/9JR3LlzecTORCQs3u3xt1eigRpVkojP/wj/zniI=',
        ed25519Priv: 'DhU9qjHh9pcTSEMh+UVKhENp1SwE0B+rOvZ6B64xG0w=',
        ed25519Pub: 'MCm92C4YIPnc8sV0y9Xya8LT8s582DbPk/EddoyRVOU=',
    },
    {
        label: 'sequential',
        seed: Uint8Array.from(Array.from({ length: 32 }, (_, i) => i)),
        x25519Priv: 'ri9ZbRNMb0SzjNxfBZBXQBaHZLbng3wL2LlbDeIQYl0=',
        x25519Pub: 'TNo8xxNR1wr2kEcDUpHPPGFY0ejFcFnMz38vVepD/XA=',
        ed25519Priv: 'iuFbXySpLbndD+opZJtoZDwH3vAnQlcnHhsfvucOLjE=',
        ed25519Pub: 'A70zgmy0TJvCGW54qLHbV+LQJu//kdFSTaZ5xIYB7aw=',
    },
];

describe('cross-language KDF vectors', () => {
    for (const v of VECTORS) {
        it(`deriveDualKeyPair matches BouncyCastle vector — ${v.label}`, async () => {
            expect(v.seed.length).toBe(32);

            const dual = await deriveDualKeyPair(v.seed);

            expect(bytesToBase64(dual.x25519PrivateKey)).toBe(v.x25519Priv);
            expect(bytesToBase64(dual.x25519PublicKey)).toBe(v.x25519Pub);
            expect(bytesToBase64(dual.ed25519PrivateKey)).toBe(v.ed25519Priv);
            expect(bytesToBase64(dual.ed25519PublicKey)).toBe(v.ed25519Pub);
        });
    }
});
