/**
 * Decode standard Base64 string to Uint8Array.
 */
export declare function base64ToBytes(base64: string): Uint8Array;
/**
 * Encode Uint8Array to standard Base64 string.
 */
export declare function bytesToBase64(bytes: Uint8Array): string;
/**
 * Convert URL-safe Base64 to standard Base64.
 */
export declare function base64UrlToBase64(base64url: string): string;
/**
 * Zero-fill a Uint8Array to securely wipe sensitive data from memory.
 */
export declare function clearBytes(bytes: Uint8Array): void;
/**
 * Execute a function with a buffer, ensuring it's zeroed after use.
 */
export declare function withSecureBuffer<T>(buffer: Uint8Array, fn: (buf: Uint8Array) => T | Promise<T>): Promise<T>;
/**
 * Concatenate multiple Uint8Arrays into a single buffer.
 */
export declare function concatBytes(...arrays: Uint8Array[]): Uint8Array;
/**
 * Convert Uint8Array to ArrayBuffer for SubtleCrypto APIs.
 * TS 5.7+ distinguishes Uint8Array<ArrayBufferLike> from BufferSource,
 * requiring an explicit slice + cast at the SubtleCrypto boundary.
 */
export declare function toBuffer(data: Uint8Array): ArrayBuffer;
/**
 * Wrap Ed25519 seed in PKCS8 ASN.1 structure for SubtleCrypto importKey.
 */
export declare function wrapSeedInPkcs8(seed: Uint8Array): Uint8Array;
/**
 * Generate cryptographically secure random bytes.
 */
export declare function generateRandomBytes(length: number): Uint8Array;
//# sourceMappingURL=utils.d.ts.map