import { describe, it, expect } from 'vitest';
import { generateVapidKeyPair, importVapidPrivateKey, createVapidAuthHeader } from '../src/crypto-core/index.js';

describe('vapid', () => {
    it('generates valid P-256 key pair', async () => {
        const keyPair = await generateVapidKeyPair();

        // Uncompressed P-256 public key is 65 bytes (0x04 prefix + 32x + 32y)
        expect(keyPair.publicKey.length).toBe(65);
        expect(keyPair.publicKey[0]).toBe(0x04);

        // PKCS8 private key should be non-empty
        expect(keyPair.privateKeyPkcs8.length).toBeGreaterThan(0);

        // CryptoKey should be usable for signing
        expect(keyPair.cryptoKey.type).toBe('private');
    });

    it('imports private key from PKCS8', async () => {
        const keyPair = await generateVapidKeyPair();
        const imported = await importVapidPrivateKey(keyPair.privateKeyPkcs8);

        expect(imported.type).toBe('private');
        expect(imported.algorithm).toMatchObject({ name: 'ECDSA', namedCurve: 'P-256' });
    });

    it('creates valid VAPID auth header', async () => {
        const keyPair = await generateVapidKeyPair();
        const header = await createVapidAuthHeader(
            'https://fcm.googleapis.com',
            'mailto:test@example.com',
            keyPair.cryptoKey,
            keyPair.publicKey
        );

        // Header format: "vapid t=<JWT>, k=<base64url>"
        expect(header).toMatch(/^vapid t=[\w-]+\.[\w-]+\.[\w-]+, k=[\w-]+$/);
    });

    it('JWT contains correct claims', async () => {
        const keyPair = await generateVapidKeyPair();
        const header = await createVapidAuthHeader(
            'https://push.example.com',
            'mailto:user@example.com',
            keyPair.cryptoKey,
            keyPair.publicKey,
            3600
        );

        // Extract and decode JWT payload
        const jwt = header.split('t=')[1].split(',')[0].trim();
        const payloadB64 = jwt.split('.')[1];
        const payload = JSON.parse(atob(payloadB64.replace(/-/g, '+').replace(/_/g, '/')));

        expect(payload.aud).toBe('https://push.example.com');
        expect(payload.sub).toBe('mailto:user@example.com');
        expect(payload.exp).toBeGreaterThan(Math.floor(Date.now() / 1000));
    });

    it('JWT signature is verifiable with public key', async () => {
        const keyPair = await generateVapidKeyPair();
        const header = await createVapidAuthHeader(
            'https://push.example.com',
            'mailto:test@example.com',
            keyPair.cryptoKey,
            keyPair.publicKey
        );

        // Extract JWT parts
        const jwt = header.split('t=')[1].split(',')[0].trim();
        const [headerB64, payloadB64, signatureB64] = jwt.split('.');
        const signingInput = `${headerB64}.${payloadB64}`;

        // Decode signature from base64url
        const sigB64 = signatureB64.replace(/-/g, '+').replace(/_/g, '/');
        const padded = sigB64 + '='.repeat((4 - sigB64.length % 4) % 4);
        const sigBytes = Uint8Array.from(atob(padded), c => c.charCodeAt(0));

        // Import public key for verification
        const publicCryptoKey = await crypto.subtle.importKey(
            'raw',
            keyPair.publicKey,
            { name: 'ECDSA', namedCurve: 'P-256' },
            false,
            ['verify']
        );

        const valid = await crypto.subtle.verify(
            { name: 'ECDSA', hash: 'SHA-256' },
            publicCryptoKey,
            sigBytes,
            new TextEncoder().encode(signingInput)
        );

        expect(valid).toBe(true);
    });
});
