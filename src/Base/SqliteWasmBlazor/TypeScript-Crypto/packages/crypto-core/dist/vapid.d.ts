/**
 * VAPID key pair (ECDSA P-256) for push notification identity.
 */
export interface VapidKeyPair {
    /** Raw ECDSA P-256 public key (65 bytes, uncompressed) */
    publicKey: Uint8Array;
    /** PKCS8-encoded private key for storage */
    privateKeyPkcs8: Uint8Array;
    /** CryptoKey handle for signing (non-extractable) */
    cryptoKey: CryptoKey;
}
/**
 * Generate a new VAPID ECDSA P-256 key pair.
 */
export declare function generateVapidKeyPair(): Promise<VapidKeyPair>;
/**
 * Import a VAPID private key from PKCS8 bytes.
 * Returns a non-extractable CryptoKey for signing.
 */
export declare function importVapidPrivateKey(pkcs8: Uint8Array): Promise<CryptoKey>;
/**
 * Create a signed VAPID Authorization header value.
 *
 * @param audience - Push service origin (e.g. "https://fcm.googleapis.com")
 * @param subject - Contact URI (e.g. "mailto:user@example.com")
 * @param privateKey - ECDSA P-256 CryptoKey for signing
 * @param publicKey - Raw 65-byte uncompressed public key
 * @param expSeconds - JWT expiry in seconds from now (default 12 hours, max 24h per spec)
 * @returns The full Authorization header value: "vapid t=<JWT>, k=<base64url pubkey>"
 */
export declare function createVapidAuthHeader(audience: string, subject: string, privateKey: CryptoKey, publicKey: Uint8Array, expSeconds?: number): Promise<string>;
//# sourceMappingURL=vapid.d.ts.map