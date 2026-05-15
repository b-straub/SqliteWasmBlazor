// crypto-header.ts
// Crypto header + CEK lifecycle + binary helpers + schema fingerprint.
//
// Extracted from crypto-ops.ts (G3.5a). No behavior change.
//
// Layer 3 of the crypto stack lives here: the wire-format CryptoHeader and
// the CEK unwrap (X25519 ECDH + HKDF + AES-GCM unwrap). Layers 1+2 (per-row
// AES-GCM + Ed25519 batch signature) live in crypto-delta.ts (delta export +
// import); permission resolution lives in crypto-permissions.ts.

import { unpack } from 'msgpackr';
import {
    deriveWrappingKey, unwrapContentKey,
    clearBytes,
    type SymmetricEncryptedData
} from '@sqlitewasmblazor/crypto-core';

// ============================================================================
// Crypto Header
// ============================================================================

export interface CryptoHeader {
    version: number;
    systemTables: string[];
    clientContactId: string | Uint8Array;
    clientX25519PrivateKey: Uint8Array;
    adminX25519PublicKey: Uint8Array;
    groupContext: string;
    keyVersion: number;
    wrappedCek: Uint8Array;
    clientEd25519PrivateKey: Uint8Array;
    clientEd25519PublicKey: Uint8Array;
}

/**
 * Parse a MessagePack-serialized CryptoHeader (version 2). Array layout:
 *   [0] Version (int, must be 2)
 *   [1] SystemTables (string[])
 *   [2] ClientContactId (Guid — 16 LE bytes or 36-char string)
 *   [3] ClientX25519PrivateKey (32 bytes)
 *   [4] AdminX25519PublicKey (32 bytes)
 *   [5] GroupContext (string)
 *   [6] KeyVersion (int)
 *   [7] WrappedCek (byte[] — [nonce(12)|ciphertext])
 *   [8] ClientEd25519PrivateKey (32 bytes)
 *   [9] ClientEd25519PublicKey (32 bytes)
 */
export function parseCryptoHeader(bytes: Uint8Array): CryptoHeader {
    const arr = unpack(bytes) as unknown;
    if (!Array.isArray(arr) || arr.length < 10) {
        throw new Error(`CryptoHeader: expected 10-element array, got length ${Array.isArray(arr) ? arr.length : typeof arr}`);
    }

    const version = arr[0];
    if (typeof version !== 'number' || version !== 2) {
        throw new Error(`CryptoHeader: unsupported version ${version}, expected 2`);
    }
    if (!Array.isArray(arr[1])) {
        throw new Error('CryptoHeader: SystemTables must be array');
    }
    if (typeof arr[2] !== 'string' && !(arr[2] instanceof Uint8Array)) {
        throw new Error(`CryptoHeader: ClientContactId must be string or Uint8Array, got ${typeof arr[2]}`);
    }
    if (!(arr[3] instanceof Uint8Array) || arr[3].byteLength !== 32) {
        throw new Error('CryptoHeader: ClientX25519PrivateKey must be 32-byte Uint8Array');
    }
    if (!(arr[4] instanceof Uint8Array) || arr[4].byteLength !== 32) {
        throw new Error('CryptoHeader: AdminX25519PublicKey must be 32-byte Uint8Array');
    }
    if (typeof arr[5] !== 'string') {
        throw new Error('CryptoHeader: GroupContext must be string');
    }
    if (typeof arr[6] !== 'number') {
        throw new Error('CryptoHeader: KeyVersion must be number');
    }
    if (!(arr[7] instanceof Uint8Array) || arr[7].byteLength < 12) {
        throw new Error('CryptoHeader: WrappedCek must be Uint8Array with at least 12 bytes');
    }
    if (!(arr[8] instanceof Uint8Array) || arr[8].byteLength !== 32) {
        throw new Error('CryptoHeader: ClientEd25519PrivateKey must be 32-byte Uint8Array');
    }
    if (!(arr[9] instanceof Uint8Array) || arr[9].byteLength !== 32) {
        throw new Error('CryptoHeader: ClientEd25519PublicKey must be 32-byte Uint8Array');
    }

    return {
        version,
        systemTables: arr[1] as string[],
        clientContactId: arr[2],
        clientX25519PrivateKey: arr[3],
        adminX25519PublicKey: arr[4],
        groupContext: arr[5],
        keyVersion: arr[6],
        wrappedCek: arr[7],
        clientEd25519PrivateKey: arr[8],
        clientEd25519PublicKey: arr[9]
    };
}

// Zero the secret-bearing fields of a parsed CryptoHeader. Public keys and
// metadata fields are not cleared. Pair with clearBytes(headerBytes) on the
// MessagePack input — msgpack-decoded Uint8Arrays alias the input buffer, so
// either call also wipes the matching range in headerBytes; clearing both is
// defense-in-depth.
export function clearCryptoHeader(h: CryptoHeader): void {
    clearBytes(h.clientX25519PrivateKey);
    clearBytes(h.clientEd25519PrivateKey);
    clearBytes(h.wrappedCek);
}

// ============================================================================
// CEK unwrap + AAD
// ============================================================================

export async function unwrapCekFromHeader(header: CryptoHeader): Promise<Uint8Array> {
    const wrappingKey = await deriveWrappingKey(
        header.clientX25519PrivateKey,
        header.adminX25519PublicKey,
        header.groupContext);
    try {
        const wrapped: SymmetricEncryptedData = {
            nonce: header.wrappedCek.subarray(0, 12),
            ciphertext: header.wrappedCek.subarray(12)
        };
        return await unwrapContentKey(wrapped, wrappingKey);
    } finally {
        clearBytes(wrappingKey);
    }
}

export function buildAad(groupContext: string, keyVersion: number): Uint8Array {
    return new TextEncoder().encode(`${groupContext}:${keyVersion}`);
}

// ============================================================================
// Binary helpers
// ============================================================================

export function bytesToHex(bytes: Uint8Array): string {
    return Array.from(bytes).map(b => b.toString(16).padStart(2, '0')).join('');
}

export function hexToBytes(hex: string): Uint8Array {
    const bytes = new Uint8Array(hex.length / 2);
    for (let i = 0; i < bytes.length; i++) {
        bytes[i] = parseInt(hex.substring(i * 2, i * 2 + 2), 16);
    }
    return bytes;
}

// ============================================================================
// Schema fingerprint
// ============================================================================

/**
 * Compute a deterministic hex hash of the _column_registry entries for a table.
 * Format: SHA-256 of "col0:sqlType0:csharpType0|col1:sqlType1:csharpType1|..."
 * ordered by ColumnIndex. Both sender and receiver compute this independently;
 * a mismatch means different app versions (different migrations).
 */
export function computeColumnRegistryHash(db: any, tableName: string): string {
    const rows = db.exec({
        sql: `SELECT ColumnName, SqlType, CSharpType FROM _column_registry WHERE TableName = ? ORDER BY ColumnIndex`,
        bind: [tableName],
        returnValue: 'resultRows',
        rowMode: 'array'
    }) as any[][];

    if (!rows || rows.length === 0) {
        return '';
    }

    const canonical = rows.map((r: any[]) => `${r[0]}:${r[1]}:${r[2]}`).join('|');
    // FNV-1a 32-bit: deterministic, sync, no crypto strength needed (version check, not a security boundary).
    let hash = 0x811c9dc5;
    for (let i = 0; i < canonical.length; i++) {
        hash ^= canonical.charCodeAt(i);
        hash = Math.imul(hash, 0x01000193);
    }
    return (hash >>> 0).toString(16).padStart(8, '0');
}
