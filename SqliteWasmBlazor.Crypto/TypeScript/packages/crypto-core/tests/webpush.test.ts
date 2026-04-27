import { describe, it, expect } from 'vitest';
import { encryptPushPayload, generateVapidKeyPair } from '../src/index.js';
import type { PushSubscriptionKeys } from '../src/index.js';

/**
 * Generate a mock push subscription (simulates browser pushManager.subscribe()).
 * Returns P-256 ECDH keys: p256dh (public) and auth (shared secret).
 */
async function createMockSubscription(): Promise<{
    keys: PushSubscriptionKeys;
    privateKey: CryptoKey;
}> {
    const keyPair = await crypto.subtle.generateKey(
        { name: 'ECDH', namedCurve: 'P-256' },
        true,
        ['deriveBits']
    );

    const p256dh = new Uint8Array(
        await crypto.subtle.exportKey('raw', keyPair.publicKey)
    );
    const auth = crypto.getRandomValues(new Uint8Array(16));

    return {
        keys: { p256dh, auth },
        privateKey: keyPair.privateKey,
    };
}

describe('webpush', () => {
    it('encrypts payload with correct aes128gcm structure', async () => {
        const subscription = await createMockSubscription();
        const plaintext = new TextEncoder().encode('{"title":"Test","body":"Hello"}');

        const encrypted = await encryptPushPayload(plaintext, subscription.keys);

        // aes128gcm header: salt(16) + rs(4) + keyIdLen(1) + keyId(65) = 86 bytes header
        expect(encrypted.length).toBeGreaterThan(86);

        // Verify salt is 16 bytes
        const salt = encrypted.slice(0, 16);
        expect(salt.length).toBe(16);

        // Verify record size is 4096 (big-endian)
        const rs = new DataView(encrypted.buffer, encrypted.byteOffset + 16, 4).getUint32(0, false);
        expect(rs).toBe(4096);

        // Verify keyId length
        const keyIdLen = encrypted[20];
        expect(keyIdLen).toBe(65); // Uncompressed P-256 public key

        // Verify sender public key starts with 0x04 (uncompressed)
        expect(encrypted[21]).toBe(0x04);
    });

    it('produces different ciphertext each time (random salt + ephemeral key)', async () => {
        const subscription = await createMockSubscription();
        const plaintext = new TextEncoder().encode('same message');

        const encrypted1 = await encryptPushPayload(plaintext, subscription.keys);
        const encrypted2 = await encryptPushPayload(plaintext, subscription.keys);

        // Different random salt → different ciphertext
        expect(encrypted1).not.toEqual(encrypted2);
    });

    it('rejects payload exceeding single-record limit', async () => {
        const subscription = await createMockSubscription();
        // 4096 - 16 (tag) - 1 (padding) = 4079 max
        const tooLarge = new Uint8Array(4080);

        await expect(encryptPushPayload(tooLarge, subscription.keys))
            .rejects.toThrow(/Payload too large/);
    });

    it('accepts maximum-size payload', async () => {
        const subscription = await createMockSubscription();
        // Exactly at the limit: 4079 bytes
        const maxPayload = new Uint8Array(4079);

        const encrypted = await encryptPushPayload(maxPayload, subscription.keys);
        expect(encrypted.length).toBeGreaterThan(86);
    });

    it('encrypts small payloads correctly', async () => {
        const subscription = await createMockSubscription();
        const tiny = new TextEncoder().encode('hi');

        const encrypted = await encryptPushPayload(tiny, subscription.keys);

        // Header (86) + ciphertext (plaintext + 1 padding + 16 tag) = 86 + 19 = 105
        expect(encrypted.length).toBe(86 + 2 + 1 + 16);
    });

    it('can decrypt with subscriber private key (round-trip verification)', async () => {
        const subscription = await createMockSubscription();
        const plaintext = new TextEncoder().encode('{"title":"Push","body":"Round trip!"}');

        const encrypted = await encryptPushPayload(plaintext, subscription.keys);

        // Parse aes128gcm header
        const salt = encrypted.slice(0, 16);
        const keyIdLen = encrypted[20];
        const senderPublicKeyRaw = encrypted.slice(21, 21 + keyIdLen);
        const ciphertext = encrypted.slice(21 + keyIdLen);

        // Import sender's ephemeral public key
        const senderPublicKey = await crypto.subtle.importKey(
            'raw',
            senderPublicKeyRaw,
            { name: 'ECDH', namedCurve: 'P-256' },
            false,
            []
        );

        // ECDH: subscriber private + sender public
        const sharedSecret = new Uint8Array(
            await crypto.subtle.deriveBits(
                { name: 'ECDH', public: senderPublicKey },
                subscription.privateKey,
                256
            )
        );

        // Derive IKM
        const encoder = new TextEncoder();
        const infoIkm = concatForTest(
            encoder.encode('WebPush: info\0'),
            subscription.keys.p256dh,
            senderPublicKeyRaw
        );
        const ikmBits = await hkdfForTest(subscription.keys.auth, sharedSecret, infoIkm, 256);

        // Derive CEK
        const infoCek = encoder.encode('Content-Encoding: aes128gcm\0');
        const cekBits = await hkdfForTest(salt, ikmBits, infoCek, 128);
        const contentKey = await crypto.subtle.importKey(
            'raw', cekBits, { name: 'AES-GCM' }, false, ['decrypt']
        );

        // Derive nonce
        const infoNonce = encoder.encode('Content-Encoding: nonce\0');
        const nonce = await hkdfForTest(salt, ikmBits, infoNonce, 96);

        // Decrypt
        const decrypted = new Uint8Array(
            await crypto.subtle.decrypt(
                { name: 'AES-GCM', iv: nonce, tagLength: 128 },
                contentKey,
                ciphertext
            )
        );

        // Remove padding delimiter (last byte should be 0x02)
        expect(decrypted[decrypted.length - 1]).toBe(0x02);
        const message = decrypted.slice(0, decrypted.length - 1);

        expect(new TextDecoder().decode(message)).toBe('{"title":"Push","body":"Round trip!"}');
    });
});

// ============================================================
// TEST HELPERS — mirror internal RFC 8291 derivation for verification
// ============================================================

function concatForTest(...arrays: Uint8Array[]): Uint8Array {
    const totalLength = arrays.reduce((acc, arr) => acc + arr.length, 0);
    const result = new Uint8Array(totalLength);
    let offset = 0;
    for (const arr of arrays) {
        result.set(arr, offset);
        offset += arr.length;
    }
    return result;
}

async function hkdfForTest(
    salt: Uint8Array,
    ikm: Uint8Array,
    info: Uint8Array,
    bits: number
): Promise<Uint8Array> {
    const baseKey = await crypto.subtle.importKey(
        'raw', ikm.buffer.slice(ikm.byteOffset, ikm.byteOffset + ikm.byteLength),
        { name: 'HKDF' }, false, ['deriveBits']
    );
    const derived = await crypto.subtle.deriveBits(
        {
            name: 'HKDF',
            hash: 'SHA-256',
            salt: salt.buffer.slice(salt.byteOffset, salt.byteOffset + salt.byteLength),
            info: info.buffer.slice(info.byteOffset, info.byteOffset + info.byteLength),
        },
        baseKey,
        bits
    );
    return new Uint8Array(derived);
}
