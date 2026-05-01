// Re-export shared types from crypto-core
export type { PrfResult } from '@sqlitewasmblazor/crypto-core';
export { PrfErrorCode } from '@sqlitewasmblazor/crypto-core';

// Bridge-only types (WebAuthn / C# interop)

export interface PrfCredential {
    id: string;
    rawId: string; // Base64 encoded
}

export interface PrfOptions {
    rpName: string;
    rpId?: string;
    timeoutMs: number;
    authenticatorAttachment: 'platform' | 'cross-platform' | 'any';
}

export interface KeyCacheConfig {
    strategy: 'none' | 'session' | 'timed';
    ttlMs: number;
}

// WebAuthn PRF extension types
export interface PrfExtensionInput {
    eval?: {
        first: ArrayBuffer;
        second?: ArrayBuffer;
    };
    evalByCredential?: Record<string, {
        first: ArrayBuffer;
        second?: ArrayBuffer;
    }>;
}

export interface PrfExtensionOutput {
    enabled?: boolean;
    results?: {
        first: ArrayBuffer;
        second?: ArrayBuffer;
    };
}
