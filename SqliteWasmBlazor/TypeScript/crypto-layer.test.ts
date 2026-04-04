import { describe, it, expect, vi, beforeEach } from 'vitest';
import {
    encryptAesGcm, decryptAesGcm,
    generateX25519KeyPair, getX25519PublicKey,
    encryptAsymmetricAesGcm, decryptAsymmetricAesGcm,
    generateEd25519KeyPair, getEd25519PublicKey, ed25519Sign, ed25519Verify,
    deriveHkdfKey, deriveDualKeyPair, deriveX25519KeyPair, deriveEd25519KeyPair,
    storeKeys, getPublicKeys, hasKey, removeKeys, clearAllKeys,
    signWithCachedKey,
    encryptSymmetricCachedAesGcm, decryptSymmetricCachedAesGcm,
    decryptAsymmetricCachedAesGcm,
    generateRandomBytes, isSupported
} from './crypto-layer';

function toBase64(data: Uint8Array): string {
    return btoa(String.fromCharCode(...data));
}

function fromBase64(base64: string): Uint8Array {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
}

function randomKeyBase64(): string {
    const key = new Uint8Array(32);
    crypto.getRandomValues(key);
    return toBase64(key);
}

function randomSeedBase64(): string {
    return randomKeyBase64();
}

const TEST_PLAINTEXT = toBase64(new TextEncoder().encode('Hello, crypto-layer!'));

describe('AES-GCM symmetric encryption', () => {
    it('encrypt/decrypt roundtrip', async () => {
        const key = randomKeyBase64();
        const encrypted = JSON.parse(await encryptAesGcm(TEST_PLAINTEXT, key));
        expect(encrypted.success).toBe(true);

        const decrypted = JSON.parse(await decryptAesGcm(encrypted.ciphertextBase64, encrypted.nonceBase64, key));
        expect(decrypted.success).toBe(true);
        expect(decrypted.plaintextBase64).toBe(TEST_PLAINTEXT);
    });

    it('wrong key fails decryption', async () => {
        const key1 = randomKeyBase64();
        const key2 = randomKeyBase64();
        const encrypted = JSON.parse(await encryptAesGcm(TEST_PLAINTEXT, key1));
        expect(encrypted.success).toBe(true);

        const decrypted = JSON.parse(await decryptAesGcm(encrypted.ciphertextBase64, encrypted.nonceBase64, key2));
        expect(decrypted.success).toBe(false);
    });

    it('tampered ciphertext fails decryption', async () => {
        const key = randomKeyBase64();
        const encrypted = JSON.parse(await encryptAesGcm(TEST_PLAINTEXT, key));
        expect(encrypted.success).toBe(true);

        // Flip a byte in the ciphertext
        const ctBytes = fromBase64(encrypted.ciphertextBase64);
        ctBytes[0] ^= 0xFF;
        const tamperedCt = toBase64(ctBytes);

        const decrypted = JSON.parse(await decryptAesGcm(tamperedCt, encrypted.nonceBase64, key));
        expect(decrypted.success).toBe(false);
    });
});

describe('X25519 key exchange', () => {
    it('generates valid 32-byte keypair', () => {
        const result = JSON.parse(generateX25519KeyPair());
        expect(result.success).toBe(true);
        expect(fromBase64(result.privateKeyBase64).length).toBe(32);
        expect(fromBase64(result.publicKeyBase64).length).toBe(32);
    });

    it('derives correct public key from private', () => {
        const result = JSON.parse(generateX25519KeyPair());
        const derivedPublic = getX25519PublicKey(result.privateKeyBase64);
        expect(derivedPublic).toBe(result.publicKeyBase64);
    });
});

describe('ECIES AES-GCM asymmetric encryption', () => {
    it('encrypt/decrypt roundtrip', async () => {
        const keypair = JSON.parse(generateX25519KeyPair());

        const encrypted = JSON.parse(await encryptAsymmetricAesGcm(TEST_PLAINTEXT, keypair.publicKeyBase64));
        expect(encrypted.success).toBe(true);
        expect(encrypted.ephemeralPublicKeyBase64).toBeDefined();

        const decrypted = JSON.parse(await decryptAsymmetricAesGcm(
            encrypted.ephemeralPublicKeyBase64,
            encrypted.ciphertextBase64,
            encrypted.nonceBase64,
            keypair.privateKeyBase64
        ));
        expect(decrypted.success).toBe(true);
        expect(decrypted.plaintextBase64).toBe(TEST_PLAINTEXT);
    });

    it('wrong private key fails decryption', async () => {
        const recipient = JSON.parse(generateX25519KeyPair());
        const wrongKey = JSON.parse(generateX25519KeyPair());

        const encrypted = JSON.parse(await encryptAsymmetricAesGcm(TEST_PLAINTEXT, recipient.publicKeyBase64));
        expect(encrypted.success).toBe(true);

        const decrypted = JSON.parse(await decryptAsymmetricAesGcm(
            encrypted.ephemeralPublicKeyBase64,
            encrypted.ciphertextBase64,
            encrypted.nonceBase64,
            wrongKey.privateKeyBase64
        ));
        expect(decrypted.success).toBe(false);
    });
});

describe('Ed25519 signing', () => {
    it('sign/verify roundtrip', () => {
        const keypair = JSON.parse(generateEd25519KeyPair());

        const signed = JSON.parse(ed25519Sign(TEST_PLAINTEXT, keypair.privateKeyBase64));
        expect(signed.success).toBe(true);

        const valid = ed25519Verify(TEST_PLAINTEXT, signed.signatureBase64, keypair.publicKeyBase64);
        expect(valid).toBe(true);
    });

    it('verify with wrong key returns false', () => {
        const keypair1 = JSON.parse(generateEd25519KeyPair());
        const keypair2 = JSON.parse(generateEd25519KeyPair());

        const signed = JSON.parse(ed25519Sign(TEST_PLAINTEXT, keypair1.privateKeyBase64));
        expect(signed.success).toBe(true);

        const valid = ed25519Verify(TEST_PLAINTEXT, signed.signatureBase64, keypair2.publicKeyBase64);
        expect(valid).toBe(false);
    });

    it('derives correct public key from private', () => {
        const keypair = JSON.parse(generateEd25519KeyPair());
        const derivedPublic = getEd25519PublicKey(keypair.privateKeyBase64);
        expect(derivedPublic).toBe(keypair.publicKeyBase64);
    });
});

describe('Key derivation (HKDF)', () => {
    it('deriveHkdfKey is deterministic', () => {
        const seed = randomSeedBase64();
        const key1 = deriveHkdfKey(seed, 'test-domain');
        const key2 = deriveHkdfKey(seed, 'test-domain');
        expect(key1).toBe(key2);
        expect(fromBase64(key1).length).toBe(32);
    });

    it('different domains produce different keys', () => {
        const seed = randomSeedBase64();
        const key1 = deriveHkdfKey(seed, 'domain-a');
        const key2 = deriveHkdfKey(seed, 'domain-b');
        expect(key1).not.toBe(key2);
    });

    it('deriveDualKeyPair is deterministic', () => {
        const seed = randomSeedBase64();
        const result1 = JSON.parse(deriveDualKeyPair(seed));
        const result2 = JSON.parse(deriveDualKeyPair(seed));

        expect(result1.success).toBe(true);
        expect(result1.x25519PublicKeyBase64).toBe(result2.x25519PublicKeyBase64);
        expect(result1.ed25519PublicKeyBase64).toBe(result2.ed25519PublicKeyBase64);
    });

    it('deriveX25519KeyPair matches deriveDualKeyPair', () => {
        const seed = randomSeedBase64();
        const dual = JSON.parse(deriveDualKeyPair(seed));
        const x25519Result = JSON.parse(deriveX25519KeyPair(seed));

        expect(x25519Result.publicKeyBase64).toBe(dual.x25519PublicKeyBase64);
    });

    it('deriveEd25519KeyPair matches deriveDualKeyPair', () => {
        const seed = randomSeedBase64();
        const dual = JSON.parse(deriveDualKeyPair(seed));
        const ed25519Result = JSON.parse(deriveEd25519KeyPair(seed));

        expect(ed25519Result.publicKeyBase64).toBe(dual.ed25519PublicKeyBase64);
    });
});

describe('Key cache', () => {
    const KEY_ID = 'test-key';

    beforeEach(() => {
        clearAllKeys();
    });

    it('store and retrieve public keys', async () => {
        const seed = randomSeedBase64();
        const storeResult = JSON.parse(await storeKeys(KEY_ID, seed, null));
        expect(storeResult.success).toBe(true);
        expect(hasKey(KEY_ID)).toBe(true);

        const pubKeys = JSON.parse(getPublicKeys(KEY_ID));
        expect(pubKeys.success).toBe(true);
        expect(pubKeys.x25519PublicKeyBase64).toBe(storeResult.x25519PublicKeyBase64);
        expect(pubKeys.ed25519PublicKeyBase64).toBe(storeResult.ed25519PublicKeyBase64);
    });

    it('removal clears key', async () => {
        const seed = randomSeedBase64();
        await storeKeys(KEY_ID, seed, null);
        expect(hasKey(KEY_ID)).toBe(true);

        removeKeys(KEY_ID);
        expect(hasKey(KEY_ID)).toBe(false);
    });

    it('TTL expiration clears key', async () => {
        vi.useFakeTimers();
        try {
            const seed = randomSeedBase64();
            await storeKeys(KEY_ID, seed, 100);
            expect(hasKey(KEY_ID)).toBe(true);

            vi.advanceTimersByTime(101);
            expect(hasKey(KEY_ID)).toBe(false);
        } finally {
            vi.useRealTimers();
        }
    });

    it('cached symmetric AES-GCM roundtrip', async () => {
        const seed = randomSeedBase64();
        await storeKeys(KEY_ID, seed, null);

        const encrypted = JSON.parse(await encryptSymmetricCachedAesGcm(KEY_ID, TEST_PLAINTEXT));
        expect(encrypted.success).toBe(true);

        const decrypted = JSON.parse(await decryptSymmetricCachedAesGcm(KEY_ID, encrypted.ciphertextBase64, encrypted.nonceBase64));
        expect(decrypted.success).toBe(true);
        expect(decrypted.plaintextBase64).toBe(TEST_PLAINTEXT);
    });

    it('cached asymmetric decrypt', async () => {
        const seed = randomSeedBase64();
        const storeResult = JSON.parse(await storeKeys(KEY_ID, seed, null));

        // Encrypt with the cached public key
        const encrypted = JSON.parse(await encryptAsymmetricAesGcm(TEST_PLAINTEXT, storeResult.x25519PublicKeyBase64));
        expect(encrypted.success).toBe(true);

        // Decrypt using the cached private key
        const decrypted = JSON.parse(await decryptAsymmetricCachedAesGcm(
            KEY_ID,
            encrypted.ephemeralPublicKeyBase64,
            encrypted.ciphertextBase64,
            encrypted.nonceBase64
        ));
        expect(decrypted.success).toBe(true);
        expect(decrypted.plaintextBase64).toBe(TEST_PLAINTEXT);
    });

    it('cached sign and verify', async () => {
        const seed = randomSeedBase64();
        const storeResult = JSON.parse(await storeKeys(KEY_ID, seed, null));

        const signed = JSON.parse(signWithCachedKey(KEY_ID, TEST_PLAINTEXT));
        expect(signed.success).toBe(true);

        const valid = ed25519Verify(TEST_PLAINTEXT, signed.signatureBase64, storeResult.ed25519PublicKeyBase64);
        expect(valid).toBe(true);
    });
});

describe('Utilities', () => {
    it('generateRandomBytes returns correct length', () => {
        const b64 = generateRandomBytes(16);
        const bytes = fromBase64(b64);
        expect(bytes.length).toBe(16);
    });

    it('generateRandomBytes produces different output', () => {
        const a = generateRandomBytes(32);
        const b = generateRandomBytes(32);
        expect(a).not.toBe(b);
    });

    it('isSupported returns true', () => {
        expect(isSupported()).toBe(true);
    });
});
