/**
 * Utility functions for secure memory handling and encoding.
 */

/**
 * Zero-fill a Uint8Array to securely wipe sensitive data from memory.
 * Call this immediately after sensitive data is no longer needed.
 */
export function zeroFill(buffer: Uint8Array): void {
    buffer.fill(0);
}

/**
 * Execute a function with a buffer, ensuring it's zeroed after use.
 * @param buffer The sensitive buffer to use
 * @param fn The function to execute with the buffer
 * @returns The result of the function
 */
export async function withSecureBuffer<T>(
    buffer: Uint8Array,
    fn: (buf: Uint8Array) => T | Promise<T>
): Promise<T> {
    try {
        return await fn(buffer);
    } finally {
        zeroFill(buffer);
    }
}

/**
 * Convert Uint8Array to Base64 string
 */
export function toBase64(data: Uint8Array): string {
    return btoa(String.fromCharCode(...data));
}

/**
 * Convert Base64 string to Uint8Array.
 * Handles both standard Base64 and URL-safe Base64.
 */
export function fromBase64(base64: string): Uint8Array {
    // Convert URL-safe Base64 to standard Base64
    let standardBase64 = base64
        .replace(/-/g, '+')
        .replace(/_/g, '/');

    // Add padding if needed
    const padding = standardBase64.length % 4;
    if (padding > 0) {
        standardBase64 += '='.repeat(4 - padding);
    }

    const binary = atob(standardBase64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
}

/**
 * Convert ArrayBuffer to Base64 string
 */
export function arrayBufferToBase64(buffer: ArrayBuffer): string {
    return toBase64(new Uint8Array(buffer));
}

/**
 * Convert Base64 string to ArrayBuffer
 */
export function base64ToArrayBuffer(base64: string): ArrayBuffer {
    return fromBase64(base64).buffer;
}

/**
 * Concatenate multiple Uint8Arrays
 */
export function concatBytes(...arrays: Uint8Array[]): Uint8Array {
    const totalLength = arrays.reduce((acc, arr) => acc + arr.length, 0);
    const result = new Uint8Array(totalLength);
    let offset = 0;
    for (const arr of arrays) {
        result.set(arr, offset);
        offset += arr.length;
    }
    return result;
}
