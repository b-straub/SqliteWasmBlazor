// crypto-ops.ts
// Encrypted export/import/rotate — crypto-core integration.
// Shadow rows ARE the wire format (no outer envelope encryption).
//
// === SECURITY LAYERS ===
//
// Layer 1 — AES-GCM per-row encryption with AAD (groupContext:keyVersion)
//   Protects: data confidentiality + per-row integrity (GCM auth tag).
//   Attacker without CEK cannot read or modify any row.
//   AAD binds each row to a specific group + key version.
//   Cost: ~2µs/row (SubtleCrypto hardware accelerated).
//
// Layer 2 — Ed25519 BATCH signature over the ShadowRowGroup
//   Protects: sender authentication. Proves the entire batch was produced
//   by the claimed sender (identified by Ed25519 public key). Prevents a
//   group member from impersonating another member (e.g., Editor setting
//   SenderPublicKey to Admin's key to bypass permission checks).
//   A ShadowRowGroup is always from ONE sender — batch signature provides
//   identical security to per-row signatures without O(N) crypto cost.
//   Cost: O(1) — one sign on export (~130µs), one verify on import (~200µs).
//   Digest: SHA-256 over all (EncryptedRow || Nonce) concatenated.
//
// Layer 3 — CEK wrapped via ECDH + HKDF (in V2CryptoHeader)
//   Protects: group membership proof. Only valid group members can unwrap
//   the CEK. Revoked members don't receive the new wrapped CEK.
//   Cost: O(1) per export/import (one ECDH + HKDF + AES-GCM unwrap).
//
// Wire format: ShadowRowGroup
//   [tableName, isSystemTable, rows[], schemaHash, batchSignature, senderPublicKeyHex]
//   Each row: [Id, SharingScope, SharingId, EncryptedRow, Nonce, KeyVersion]
//

import { logger } from './sqlite-logger';
import { pack, unpack } from 'msgpackr';
import {
    deriveWrappingKey, unwrapContentKey,
    encryptAesGcm, decryptAesGcm,
    signBatch, verifyBatch,
    ed25519Verify,
    clearBytes,
    type SymmetricEncryptedData
} from '@sqlitewasmblazor/crypto-core';
import { sha256 } from '@sqlitewasmblazor/crypto-core';
import { openDatabases, sqlite3, bigIntUnpackr, MODULE_NAME } from './worker-state';
import { convertValueForSqlite, convertValueFromSqlite } from './type-conversion';
import { bulkInsertRows } from './bulk-ops';

// ============================================================================
// V2 Crypto Header
// ============================================================================

interface V2CryptoHeader {
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
 * Parse a MessagePack-serialized V2CryptoHeader (version 2). Array layout:
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
function parseV2CryptoHeader(bytes: Uint8Array): V2CryptoHeader {
    const arr = unpack(bytes) as unknown;
    if (!Array.isArray(arr) || arr.length < 10) {
        throw new Error(`V2CryptoHeader: expected 10-element array, got length ${Array.isArray(arr) ? arr.length : typeof arr}`);
    }

    const version = arr[0];
    if (typeof version !== 'number' || version !== 2) {
        throw new Error(`V2CryptoHeader: unsupported version ${version}, expected 2`);
    }
    if (!Array.isArray(arr[1])) {
        throw new Error('V2CryptoHeader: SystemTables must be array');
    }
    if (typeof arr[2] !== 'string' && !(arr[2] instanceof Uint8Array)) {
        throw new Error(`V2CryptoHeader: ClientContactId must be string or Uint8Array, got ${typeof arr[2]}`);
    }
    if (!(arr[3] instanceof Uint8Array) || arr[3].byteLength !== 32) {
        throw new Error('V2CryptoHeader: ClientX25519PrivateKey must be 32-byte Uint8Array');
    }
    if (!(arr[4] instanceof Uint8Array) || arr[4].byteLength !== 32) {
        throw new Error('V2CryptoHeader: AdminX25519PublicKey must be 32-byte Uint8Array');
    }
    if (typeof arr[5] !== 'string') {
        throw new Error('V2CryptoHeader: GroupContext must be string');
    }
    if (typeof arr[6] !== 'number') {
        throw new Error('V2CryptoHeader: KeyVersion must be number');
    }
    if (!(arr[7] instanceof Uint8Array) || arr[7].byteLength < 12) {
        throw new Error('V2CryptoHeader: WrappedCek must be Uint8Array with at least 12 bytes');
    }
    if (!(arr[8] instanceof Uint8Array) || arr[8].byteLength !== 32) {
        throw new Error('V2CryptoHeader: ClientEd25519PrivateKey must be 32-byte Uint8Array');
    }
    if (!(arr[9] instanceof Uint8Array) || arr[9].byteLength !== 32) {
        throw new Error('V2CryptoHeader: ClientEd25519PublicKey must be 32-byte Uint8Array');
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

// ============================================================================
// Crypto helpers
// ============================================================================

async function unwrapCekFromHeader(header: V2CryptoHeader): Promise<Uint8Array> {
    const wrappingKey = deriveWrappingKey(
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

function buildAad(groupContext: string, keyVersion: number): Uint8Array {
    return new TextEncoder().encode(`${groupContext}:${keyVersion}`);
}

function bytesToHex(bytes: Uint8Array): string {
    return Array.from(bytes).map(b => b.toString(16).padStart(2, '0')).join('');
}

function hexToBytes(hex: string): Uint8Array {
    const bytes = new Uint8Array(hex.length / 2);
    for (let i = 0; i < bytes.length; i++) {
        bytes[i] = parseInt(hex.substring(i * 2, i * 2 + 2), 16);
    }
    return bytes;
}

function guidToBytes(value: unknown): Uint8Array {
    if (value instanceof Uint8Array) {
        if (value.byteLength !== 16) {
            throw new Error(`guidToBytes: Uint8Array must be 16 bytes, got ${value.byteLength}`);
        }
        return value;
    }
    if (typeof value === 'string') {
        const hex = value.replace(/-/g, '');
        if (hex.length !== 32) {
            throw new Error(`guidToBytes: string must be 32 hex chars, got ${hex.length}`);
        }
        const bytes = new Uint8Array(16);
        for (let i = 0; i < 16; i++) {
            bytes[i] = parseInt(hex.substring(i * 2, i * 2 + 2), 16);
        }
        const swap = (a: number, b: number) => { const t = bytes[a]; bytes[a] = bytes[b]; bytes[b] = t; };
        swap(0, 3); swap(1, 2);
        swap(4, 5);
        swap(6, 7);
        return bytes;
    }
    throw new Error(`guidToBytes: unsupported Guid shape ${typeof value}`);
}

function importErrorCodeToInt(code: string): number {
    switch (code) {
        case 'TAMPER_SIGNATURE_INVALID': return 1;
        case 'TAMPER_CEK_UNWRAP_FAILED': return 2;
        case 'TAMPER_AAD_MISMATCH': return 3;
        case 'TAMPER_DECRYPT_FAILED': return 4;
        case 'PERMISSION_INSERT_DENIED': return 10;
        case 'PERMISSION_UPDATE_DENIED': return 11;
        case 'PERMISSION_DELETE_DENIED': return 12;
        case 'PERMISSION_COLUMN_READONLY': return 13;
        case 'UNKNOWN_GROUP': return 20;
        default: return 99;
    }
}

// ============================================================================
// Schema versioning
// ============================================================================

/**
 * Compute a deterministic hex hash of the _column_registry entries for a table.
 * Format: SHA-256 of "col0:sqlType0:csharpType0|col1:sqlType1:csharpType1|..."
 * ordered by ColumnIndex. Both sender and receiver compute this independently;
 * a mismatch means different app versions (different migrations).
 */
function computeColumnRegistryHash(db: any, tableName: string): string {
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
    // Use synchronous FNV-1a 32-bit hash — fast, deterministic, no async needed.
    // Cryptographic strength not required — this is a version check, not a security boundary.
    let hash = 0x811c9dc5;
    for (let i = 0; i < canonical.length; i++) {
        hash ^= canonical.charCodeAt(i);
        hash = Math.imul(hash, 0x01000193);
    }
    return (hash >>> 0).toString(16).padStart(8, '0');
}

// ============================================================================
// Admin verification + Permission enforcement helpers
// ============================================================================

/**
 * Verify the sender of the most recent shadow row is the admin device.
 * Admin's Ed25519 public key is found via: Contacts WHERE IsAdmin = 1.
 * The sender's Ed25519 key (hex) is stored in the shadow table's SenderPublicKey column.
 */
function verifySenderIsAdmin(db: any, senderEd25519Hex: string): boolean {
    // Get admin's Ed25519 public key (base64) from Contacts
    const adminRows = db.exec({
        sql: `SELECT Ed25519PublicKey FROM Contacts WHERE IsAdmin = 1 LIMIT 1`,
        returnValue: 'resultRows',
        rowMode: 'array'
    }) as any[][];

    if (!adminRows || adminRows.length === 0) {
        logger.warn(MODULE_NAME, 'verifySenderIsAdmin: no admin contact found');
        return false;
    }

    const adminEd25519Base64 = adminRows[0][0] as string;

    // Convert admin's base64 to hex for comparison
    const adminBytes = Uint8Array.from(atob(adminEd25519Base64), c => c.charCodeAt(0));
    const adminHex = bytesToHex(adminBytes);

    return senderEd25519Hex === adminHex;
}

/**
 * Verify the AdminSignature on a ShareTarget credential (Step 2b).
 * Canonical payload: `memberPublicKeyBase64 | role | groupContext | keyVersion`
 * Verified against GroupAdminEd25519PublicKey on the ShareTarget row.
 */
function verifyShareTargetCredential(
    memberPublicKeyBase64: string,
    role: number,
    groupContext: string,
    keyVersion: number,
    adminSignature: Uint8Array,
    groupAdminEd25519PublicKeyBase64: string
): boolean {
    const canonical = `${memberPublicKeyBase64}|${role}|${groupContext}|${keyVersion}`;
    const canonicalBytes = new TextEncoder().encode(canonical);
    const pubKeyBytes = Uint8Array.from(atob(groupAdminEd25519PublicKeyBase64), c => c.charCodeAt(0));
    return ed25519Verify(adminSignature, canonicalBytes, pubKeyBytes);
}

/**
 * Verify that a GroupAdmin's Ed25519 public key belongs to a TrustedContact (Step 2c).
 * Returns true if a non-deleted contact with this Ed25519 key exists.
 */
function verifyGroupAdminIsTrusted(db: any, groupAdminEd25519PublicKeyBase64: string): boolean {
    const rows = db.exec({
        sql: `SELECT Id FROM Contacts WHERE Ed25519PublicKey = ? AND IsDeleted = 0 LIMIT 1`,
        bind: [groupAdminEd25519PublicKeyBase64],
        returnValue: 'resultRows',
        rowMode: 'array'
    }) as any[][];
    return rows && rows.length > 0;
}

/**
 * Session cache for permission table signature verification (Step 2d).
 * Once verified, the result is cached for the lifetime of the worker.
 * Reset on worker restart (page reload).
 */
let permissionTableVerified: boolean | null = null;

/**
 * Verify the permission table signature (Step 2d). Reads PermissionTableSignature
 * row, recomputes the canonical SHA-256 hash over all Permissions rows, verifies
 * the hash matches and the Admin's Ed25519 signature is valid.
 *
 * Canonical format matches C# PermissionTableHash.Compute():
 * Rows sorted by (TableName, Role), each: `TableName|Role|CanInsert|CanRead|CanUpdate|CanDelete|ReadonlyColumns|ReadwriteColumns\n`
 */
function verifyPermissionTableSignature(db: any): boolean {
    // Return cached result if already verified this session
    if (permissionTableVerified !== null) {
        return permissionTableVerified;
    }

    // Read the signature row
    const sigRows = db.exec({
        sql: `SELECT PermissionHash, AdminSignature, AdminEd25519PublicKey FROM PermissionSignatures LIMIT 1`,
        returnValue: 'resultRows',
        rowMode: 'array'
    }) as any[][];

    if (!sigRows || sigRows.length === 0) {
        logger.warn(MODULE_NAME, 'verifyPermissionTableSignature: no PermissionTableSignature row found');
        permissionTableVerified = false;
        return false;
    }

    const storedHash = sigRows[0][0] as Uint8Array;
    const adminSignature = sigRows[0][1] as Uint8Array;
    const adminEd25519PubBase64 = sigRows[0][2] as string;

    // Read all permission rows, sorted canonically
    const permRows = db.exec({
        sql: `SELECT TableName, Role, CanInsert, CanRead, CanUpdate, CanDelete, ReadonlyColumns, ReadwriteColumns
              FROM Permissions ORDER BY TableName, Role`,
        returnValue: 'resultRows',
        rowMode: 'array'
    }) as any[][];

    if (!permRows || permRows.length === 0) {
        logger.warn(MODULE_NAME, 'verifyPermissionTableSignature: no Permissions rows found');
        permissionTableVerified = false;
        return false;
    }

    // Compute canonical hash — must match C# PermissionTableHash.Compute()
    let canonical = '';
    for (const row of permRows) {
        const tableName = row[0] as string;
        const role = row[1] as number;
        const canInsert = row[2] ? '1' : '0';
        const canRead = row[3] ? '1' : '0';
        const canUpdate = row[4] ? '1' : '0';
        const canDelete = row[5] ? '1' : '0';
        const readonlyCols = (row[6] as string) ?? '';
        const readwriteCols = (row[7] as string) ?? '';
        canonical += `${tableName}|${role}|${canInsert}|${canRead}|${canUpdate}|${canDelete}|${readonlyCols}|${readwriteCols}\n`;
    }

    const computedHash = sha256(new TextEncoder().encode(canonical));

    // Compare hashes
    if (storedHash.length !== computedHash.length) {
        logger.warn(MODULE_NAME, 'verifyPermissionTableSignature: hash length mismatch');
        permissionTableVerified = false;
        return false;
    }
    for (let i = 0; i < storedHash.length; i++) {
        if (storedHash[i] !== computedHash[i]) {
            logger.warn(MODULE_NAME, 'verifyPermissionTableSignature: hash mismatch — permission table tampered');
            permissionTableVerified = false;
            return false;
        }
    }

    // Verify Admin's Ed25519 signature over the hash
    const hashBase64 = btoa(Array.from(computedHash).map(b => String.fromCharCode(b)).join(''));
    const messageBytes = new TextEncoder().encode(hashBase64);
    const pubKeyBytes = Uint8Array.from(atob(adminEd25519PubBase64), c => c.charCodeAt(0));

    if (!ed25519Verify(adminSignature, messageBytes, pubKeyBytes)) {
        logger.warn(MODULE_NAME, 'verifyPermissionTableSignature: Admin signature invalid');
        permissionTableVerified = false;
        return false;
    }

    logger.info(MODULE_NAME, `✓ Permission table verified: ${permRows.length} rows, signature valid`);
    permissionTableVerified = true;
    return true;
}

interface ParsedPermissions {
    insertDenied: boolean;
    updateDenied: boolean;
    deleteDenied: boolean;
    readDenied: boolean;
    /** Columns that this role may NOT update (table-level update allowed but column denied). */
    readonlyColumns: string[];
    /** Columns that this role MAY update even when table-level update is denied. */
    readwriteColumns: string[];
}

/**
 * Resolve the sender's role and parse the applicable permission diff for a domain table.
 * Returns null if the sender's role can't be determined or its credential chain is invalid.
 * The import caller must treat null as an authorization failure.
 *
 * Lookup chain:
 *   1. SenderPublicKey (Ed25519 hex) → Contacts.Ed25519PublicKey → Contact.X25519PublicKey
 *   2. X25519PublicKey + ShareGroup(groupContext) → ShareTarget.Role
 *   3. Role + TableName → Permissions.PermissionDiffJson
 */
function resolveSenderPermissions(
    db: any, tableName: string,
    senderEd25519Hex: string,
    header: { groupContext: string }
): ParsedPermissions | null {
    // Step 1: Ed25519 hex → Contact → X25519PublicKey
    // Convert hex back to base64 for Contact lookup
    const ed25519Bytes = hexToBytes(senderEd25519Hex);
    const ed25519Base64 = btoa(Array.from(ed25519Bytes).map(b => String.fromCharCode(b)).join(''));

    const contactRows = db.exec({
        sql: `SELECT X25519PublicKey FROM Contacts WHERE Ed25519PublicKey = ? LIMIT 1`,
        bind: [ed25519Base64],
        returnValue: 'resultRows',
        rowMode: 'array'
    }) as any[][];

    if (!contactRows || contactRows.length === 0) {
        logger.warn(MODULE_NAME, `resolveSenderPermissions: sender contact not found for Ed25519 key`);
        return null;
    }

    const senderX25519PubKey = contactRows[0][0] as string;

    // Step 2a: X25519PubKey + ShareGroup → ShareTarget (with credential fields)
    const targetRows = db.exec({
        sql: `SELECT st.Role, st.AdminSignature, st.GroupAdminEd25519PublicKey, st.KeyVersion, sg.GroupContext
              FROM ShareTargets st
              JOIN ShareGroups sg ON st.ShareGroupId = sg.Id
              WHERE st.MemberPublicKey = ? AND sg.GroupContext = ? AND st.KeyVersion = sg.KeyVersion
              LIMIT 1`,
        bind: [senderX25519PubKey, header.groupContext],
        returnValue: 'resultRows',
        rowMode: 'array'
    }) as any[][];

    if (!targetRows || targetRows.length === 0) {
        logger.warn(MODULE_NAME, `resolveSenderPermissions: no ShareTarget for sender in group ${header.groupContext}`);
        return null;
    }

    const senderRole = targetRows[0][0] as number; // 0=Owner, 1=Editor, 2=Viewer
    const adminSignature = targetRows[0][1] as Uint8Array | null;
    const groupAdminEd25519PubKey = targetRows[0][2] as string;
    const keyVersion = targetRows[0][3] as number;
    const groupContext = targetRows[0][4] as string;

    // Step 2b: Verify AdminSignature on the ShareTarget credential.
    // If the signature is present and non-empty, verify it. Empty signatures
    // are rejected — every ShareTarget must carry a valid credential.
    if (!adminSignature || adminSignature.length === 0) {
        logger.warn(MODULE_NAME, `resolveSenderPermissions: ShareTarget missing AdminSignature for ${header.groupContext}`);
        return null;
    }

    if (!verifyShareTargetCredential(
        senderX25519PubKey, senderRole, groupContext, keyVersion,
        adminSignature, groupAdminEd25519PubKey)) {
        logger.warn(MODULE_NAME, `resolveSenderPermissions: ShareTarget AdminSignature invalid for ${header.groupContext}`);
        return null;
    }

    // Step 2c: Verify the GroupAdmin who signed this credential is a trusted contact.
    if (!verifyGroupAdminIsTrusted(db, groupAdminEd25519PubKey)) {
        logger.warn(MODULE_NAME, `resolveSenderPermissions: GroupAdmin ${groupAdminEd25519PubKey.substring(0, 12)}… is not a trusted contact`);
        return null;
    }

    // Step 2d: Verify permission table integrity before consulting it.
    if (!verifyPermissionTableSignature(db)) {
        logger.warn(MODULE_NAME, `resolveSenderPermissions: permission table signature invalid — rejecting`);
        return null;
    }

    // Step 4: Role + TableName → fully resolved permission columns
    const permRows = db.exec({
        sql: `SELECT CanInsert, CanRead, CanUpdate, CanDelete, ReadonlyColumns, ReadwriteColumns
              FROM Permissions WHERE Role = ? AND TableName = ? AND RecordId IS NULL LIMIT 1`,
        bind: [senderRole, tableName],
        returnValue: 'resultRows',
        rowMode: 'array'
    }) as any[][];

    if (!permRows || permRows.length === 0) {
        // No permission row = full access
        return { insertDenied: false, updateDenied: false, deleteDenied: false, readDenied: false, readonlyColumns: [], readwriteColumns: [] };
    }

    const row = permRows[0];
    const splitCols = (csv: string) => csv ? csv.split(',').filter(c => c.length > 0) : [];

    return {
        insertDenied: !row[0],
        readDenied: !row[1],
        updateDenied: !row[2],
        deleteDenied: !row[3],
        readonlyColumns: splitCols(row[4] as string),
        readwriteColumns: splitCols(row[5] as string)
    };
}

/**
 * Get all column names that differ between the incoming row and the existing row.
 * Used for readwrite-override enforcement: when table-level update is denied but
 * specific columns have readwrite override, only those columns may change.
 */
function getChangedColumns(
    db: any, tableName: string, pkColumn: string, pkValue: any,
    columnNames: string[], incomingRow: any[]
): string[] {
    const selectCols = columnNames.map(c => `"${c}"`).join(', ');
    const existing = db.exec({
        sql: `SELECT ${selectCols} FROM "${tableName}" WHERE "${pkColumn}" = ? LIMIT 1`,
        bind: [pkValue],
        returnValue: 'resultRows',
        rowMode: 'array'
    }) as any[][];

    if (!existing || existing.length === 0) {
        return []; // New row — no changes to check
    }

    // Sync infrastructure columns always change with any update — exclude from
    // permission checks. They're not subject to column-level permissions.
    const syncColumns = new Set(['UpdatedAt', 'IsDeleted', 'DeletedAt', 'SharingScope', 'SharingId']);

    const changed: string[] = [];
    for (let i = 0; i < columnNames.length; i++) {
        if (columnNames[i] === pkColumn) { continue; }
        if (syncColumns.has(columnNames[i])) { continue; }
        if (String(existing[0][i]) !== String(incomingRow[i])) {
            changed.push(columnNames[i]);
        }
    }
    return changed;
}

/**
 * Check if any readonly columns were mutated in an update.
 * Compares the incoming row values against the existing row in the open table.
 * Returns the list of violated column names.
 */
function checkColumnPermissions(
    db: any, tableName: string, pkColumn: string, pkValue: any,
    columnNames: string[], incomingRow: any[], readonlyColumns: string[]
): string[] {
    const roCols = readonlyColumns.filter(c => columnNames.includes(c));
    if (roCols.length === 0) {
        return [];
    }

    const selectCols = roCols.map(c => `"${c}"`).join(', ');
    const existing = db.exec({
        sql: `SELECT ${selectCols} FROM "${tableName}" WHERE "${pkColumn}" = ? LIMIT 1`,
        bind: [pkValue],
        returnValue: 'resultRows',
        rowMode: 'array'
    }) as any[][];

    if (!existing || existing.length === 0) {
        return []; // New row — column checks don't apply to inserts
    }

    const violations: string[] = [];
    for (let i = 0; i < roCols.length; i++) {
        const colIdx = columnNames.indexOf(roCols[i]);
        if (colIdx < 0) { continue; }

        const oldVal = existing[0][i];
        const newVal = incomingRow[colIdx];

        // Compare with type coercion (SQLite stores may differ from msgpack types)
        if (String(oldVal) !== String(newVal)) {
            violations.push(roCols[i]);
        }
    }

    return violations;
}

// ============================================================================
// Encrypted export
// ============================================================================

interface TableExportSpec {
    tableName: string;
    where?: string | null;
    whereParams?: string[] | null;
    isSystemTable?: boolean;
}

/**
 * Encrypt every row of a single table into one ShadowRowGroup, using the
 * caller-provided WHERE clause (e.g. `"UpdatedAt" > ?`) bound with the
 * spec's whereParams. When `spec.where` is null/empty the full table is
 * exported.
 *
 * Returns the packed ShadowRowGroup tuple:
 *   [tableName, isSystemTable, rows, schemaHash, batchSignature, senderPublicKeyHex]
 * or `null` when the filter selected no rows.
 */
async function encryptTableGroup(
    db: any,
    spec: TableExportSpec,
    cryptoHeader: V2CryptoHeader,
    cek: Uint8Array
): Promise<unknown[] | null> {
    const tableName = spec.tableName;
    const cryptoTableName = `_crypto_${tableName}`;

    const colRows = db.exec({
        sql: `SELECT ColumnName, SqlType, CSharpType, IsPrimaryKey FROM _column_registry WHERE TableName = ? ORDER BY ColumnIndex`,
        bind: [tableName],
        returnValue: 'resultRows',
        rowMode: 'array'
    }) as any[][];

    if (!colRows || colRows.length === 0) {
        throw new Error(`deltaExportEncrypted: no _column_registry entries for table '${tableName}'`);
    }

    const columnNames = colRows.map((r: any[]) => r[0] as string);
    const sqlTypes = colRows.map((r: any[]) => r[1] as string);
    const csharpTypes = colRows.map((r: any[]) => r[2] as string);
    const colCount = colRows.length;

    const idIdx = columnNames.indexOf('Id');
    const scopeIdx = columnNames.indexOf('SharingScope');
    const sharingIdIdx = columnNames.indexOf('SharingId');
    if (idIdx < 0 || scopeIdx < 0 || sharingIdIdx < 0) {
        throw new Error(
            `deltaExportEncrypted: ${tableName} is not a SyncableEntity (missing Id/SharingScope/SharingId)`);
    }

    const tableCheck = db.exec({
        sql: `SELECT name FROM sqlite_master WHERE type='table' AND name=?`,
        bind: [cryptoTableName],
        returnValue: 'resultRows',
        rowMode: 'array'
    });
    if (!tableCheck || tableCheck.length === 0) {
        throw new Error(`deltaExportEncrypted: shadow table ${cryptoTableName} not found`);
    }

    const whereClause = (spec.where && spec.where.length > 0) ? ` WHERE ${spec.where}` : '';
    const whereParams = spec.whereParams ?? null;

    const selectCols = columnNames.map(c => `"${c}"`).join(', ');
    const selectSql = `SELECT ${selectCols} FROM "${tableName}"${whereClause}`;
    logger.info(MODULE_NAME, `deltaExportEncrypted: "${tableName}" — ${selectSql.substring(0, 120)}`);

    const isInt64Col = csharpTypes.map(t => {
        const base = t.endsWith('?') ? t.slice(0, -1) : t;
        return base === 'Int64' || base === 'UInt64';
    });
    const SQLITE_TEXT = sqlite3!.capi.SQLITE_TEXT;

    const rows: any[][] = [];
    const readStmt = db.prepare(selectSql);
    try {
        if (whereParams && whereParams.length > 0) {
            readStmt.bind(whereParams);
        }
        while (readStmt.step()) {
            const row: any[] = [];
            for (let i = 0; i < colCount; i++) {
                if (isInt64Col[i]) {
                    const textVal = readStmt.get(i, SQLITE_TEXT);
                    row.push(textVal !== null ? BigInt(textVal as string) : null);
                } else {
                    row.push(readStmt.get(i));
                }
            }
            rows.push(row);
        }
    } finally {
        readStmt.finalize();
    }

    if (rows.length === 0) {
        return null;
    }

    const convertedRows = rows.map(row =>
        row.map((val, idx) => convertValueFromSqlite(val, csharpTypes[idx], sqlTypes[idx])));

    const isSystemTable = !!spec.isSystemTable;
    const aad = buildAad(cryptoHeader.groupContext, cryptoHeader.keyVersion);
    const senderPubKeyHex = bytesToHex(cryptoHeader.clientEd25519PublicKey);

    // Layer 1: encrypt each row with AES-GCM + AAD, upsert into shadow table
    const shadowSql =
        `INSERT OR REPLACE INTO "${cryptoTableName}" ` +
        `(Id, SharingScope, SharingId, EncryptedRow, Nonce, KeyVersion, SenderPublicKey, EnvelopeSignature) ` +
        `VALUES (?, ?, ?, ?, ?, ?, ?, ?)`;
    const stmt = db.prepare(shadowSql);

    const shadowRowArrays: unknown[][] = [];
    const batchCiphertexts: Uint8Array[] = [];
    const batchNonces: Uint8Array[] = [];

    db.exec('BEGIN');
    try {
        for (let i = 0; i < convertedRows.length; i++) {
            const row = convertedRows[i];
            const rowScope = Number(row[scopeIdx]);
            const rowSharingId = String(row[sharingIdIdx]);

            const rowBytes = pack(row);
            const encrypted = await encryptAesGcm(rowBytes, cek, aad);

            batchCiphertexts.push(encrypted.ciphertext);
            batchNonces.push(encrypted.nonce);

            const emptySignature = new Uint8Array(0);
            stmt.bind([
                row[idIdx], rowScope, rowSharingId,
                encrypted.ciphertext, encrypted.nonce,
                cryptoHeader.keyVersion, senderPubKeyHex, emptySignature
            ]);
            stmt.step();
            stmt.reset();

            // Wire format: 6 elements per row (no per-row sig/sender)
            shadowRowArrays.push([
                row[idIdx], rowScope, rowSharingId,
                encrypted.ciphertext, encrypted.nonce,
                cryptoHeader.keyVersion
            ]);
        }
        stmt.finalize();
        db.exec('COMMIT');
    } catch (e) {
        try { stmt.finalize(); } catch { /* ignore */ }
        try { db.exec('ROLLBACK'); } catch { /* ignore */ }
        logger.error(MODULE_NAME, `deltaExportEncrypted: shadow upsert failed in ${cryptoTableName}:`, e);
        throw e;
    }

    // Layer 2: batch signature — single Ed25519 sign over SHA-256 of all ciphertexts
    const batchSignature = signBatch(batchCiphertexts, batchNonces, cryptoHeader.clientEd25519PrivateKey);
    const schemaHash = computeColumnRegistryHash(db, tableName);

    logger.info(MODULE_NAME,
        `✓ deltaExportEncrypted: ${tableName} → ${shadowRowArrays.length} rows`);

    // Wire format: [tableName, isSystemTable, rows, schemaHash, batchSignature, senderPublicKeyHex]
    return [tableName, isSystemTable, shadowRowArrays, schemaHash, batchSignature, senderPubKeyHex];
}

/**
 * Encrypted delta export. The caller (C#) provides a per-table spec list
 * with WHERE clauses already constructed — for a delta this is
 * <code>"UpdatedAt" &gt; ?</code>, for a full snapshot it's null. The worker
 * iterates the specs in order (caller is expected to order system-first),
 * encrypts rows per table, per-table batch-signs each group, and returns a
 * single packed <c>DeltaEnvelope</c> for the whole export.
 *
 * Wire format — DeltaEnvelope:
 *   [version=1, senderEd25519PubHex, outerSignature, groups[]]
 * where each <c>groups[i]</c> is a ShadowRowGroup tuple produced by
 * <see cref="encryptTableGroup"/>. <c>outerSignature</c> is an Ed25519
 * <c>signBatch</c> over the packed bytes of the groups array (one batched
 * element, zero-length nonce) — the identical bytes the importer recomputes
 * and verifies.
 *
 * Current implementation assumes one CEK per call (resolved from the
 * header's groupContext/keyVersion/wrappedCek, same as the single-table
 * flow that shipped). Multi-group-per-envelope CEK resolution via per-row
 * SharingId → ShareGroup lookup is a follow-up.
 */
export async function deltaExportEncrypted(dbName: string, headerBytes: Uint8Array, metadata: any) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    const tables: TableExportSpec[] = Array.isArray(metadata?.tables) ? metadata.tables : [];
    if (tables.length === 0) {
        throw new Error('deltaExportEncrypted: metadata.tables is empty — nothing to export');
    }

    const cryptoHeader = parseV2CryptoHeader(headerBytes);
    let cek: Uint8Array | null = null;

    try {
        cek = await unwrapCekFromHeader(cryptoHeader);

        const groups: unknown[][] = [];
        for (const spec of tables) {
            const group = await encryptTableGroup(db, spec, cryptoHeader, cek);
            if (group !== null) {
                groups.push(group);
            }
        }

        // Outer envelope signature: Ed25519 signBatch over the packed groups.
        // The byte layout signed is `pack(groups)` as a single-element batch
        // with a zero-length nonce — the importer recomputes the same bytes.
        const senderPubKeyHex = bytesToHex(cryptoHeader.clientEd25519PublicKey);
        const packedGroups = pack(groups);
        const outerSignature = signBatch(
            [packedGroups],
            [new Uint8Array(0)],
            cryptoHeader.clientEd25519PrivateKey);

        // Wire format: [version, senderEd25519PubHex, outerSignature, groups]
        const envelope = pack([1, senderPubKeyHex, outerSignature, groups]);

        logger.info(MODULE_NAME,
            `✓ deltaExportEncrypted: delta envelope → ${groups.length} group(s), ${envelope.length} bytes`);
        return { rawBinary: true, data: envelope };
    } finally {
        if (cek) { clearBytes(cek); }
    }
}

// ============================================================================
// Encrypted import
// ============================================================================

interface ImportErrorRow {
    code: string;
    table: string;
    rowId: string;
    groupId: string;
    message: string;
}

interface GroupApplyResult {
    rowsImported: number;
    rowsSkipped: number;
    rowsDeleted: number;
    errors: ImportErrorRow[];
}

/**
 * Apply a single decoded ShadowRowGroup: verify per-group batch signature,
 * decrypt rows, enforce permissions, upsert shadow + open tables.
 * Returns partial counters + errors for aggregation by the caller.
 *
 * `group` is the unpacked wire tuple:
 *   [tableName, isSystemTable, rows, schemaHash, batchSignature, senderPubKeyHex]
 */
async function applyShadowRowGroup(
    db: any,
    group: unknown[],
    header: V2CryptoHeader,
    cek: Uint8Array
): Promise<GroupApplyResult> {
    const errors: ImportErrorRow[] = [];
    let rowsImported = 0;
    let rowsSkipped = 0;
    let rowsDeleted = 0;

    if (!Array.isArray(group) || group.length < 3) {
        throw new Error('deltaImportEncrypted: invalid ShadowRowGroup');
    }
    const tableName = group[0] as string;
    const shadowRows = group[2] as unknown[][];
    const cryptoTableName = `_crypto_${tableName}`;

        // Schema version check: compare sender's column registry hash against local.
        // Rejects deltas from clients running a different app version (different migrations).
        if (group.length >= 4 && group[3]) {
            const senderHash = group[3] as string;
            const localHash = computeColumnRegistryHash(db, tableName);
            if (senderHash !== localHash) {
                throw new Error(
                    `deltaImportEncrypted: schema mismatch for table '${tableName}' — ` +
                    `sender hash ${senderHash.substring(0, 16)}… ≠ local hash ${localHash.substring(0, 16)}…. ` +
                    `All clients must run the same app version.`);
            }
        }

        const aad = buildAad(header.groupContext, header.keyVersion);

        // Layer 2: verify batch signature (O(1) — single Ed25519 verify)
        // The batch signature proves all rows came from the claimed sender.
        const batchSignature = group.length >= 5 ? group[4] as Uint8Array : null;
        const senderPubKeyHex = group.length >= 6 ? group[5] as string : null;

        if (!batchSignature || !senderPubKeyHex) {
            throw new Error('deltaImportEncrypted: ShadowRowGroup missing batch signature or sender key');
        }

        const ciphertexts = shadowRows.map((sr: any[]) => sr[3] as Uint8Array);
        const nonces = shadowRows.map((sr: any[]) => sr[4] as Uint8Array);
        const senderPubKeyBytes = hexToBytes(senderPubKeyHex);

        if (!verifyBatch(ciphertexts, nonces, batchSignature, senderPubKeyBytes)) {
            errors.push({
                code: 'TAMPER_SIGNATURE_INVALID',
                table: tableName, rowId: '*', groupId: header.groupContext,
                message: `Batch Ed25519 signature invalid — entire ShadowRowGroup rejected`
            });
            return { rowsImported: 0, rowsSkipped: shadowRows.length, rowsDeleted: 0, errors };
        }

        // Phase 1: Decrypt all rows (Layer 1 — AES-GCM with AAD).
        // Batch signature already verified — individual rows just need decryption.
        const verifiedRows: { sr: any[]; row: any[] }[] = [];

        for (let i = 0; i < shadowRows.length; i++) {
            const sr = shadowRows[i] as any[];
            const rowCiphertext = sr[3] as Uint8Array;
            const rowNonce = sr[4] as Uint8Array;

            // Layer 1: decrypt with AAD
            let plainRowBytes: Uint8Array;
            try {
                plainRowBytes = await decryptAesGcm({ ciphertext: rowCiphertext, nonce: rowNonce }, cek, aad);
            } catch (e) {
                const rowId = sr[0];
                const rowIdHex = rowId instanceof Uint8Array ? bytesToHex(rowId) : String(rowId);
                errors.push({
                    code: 'TAMPER_AAD_MISMATCH',
                    table: tableName, rowId: rowIdHex, groupId: header.groupContext,
                    message: `AES-GCM decrypt failed: ${e instanceof Error ? e.message : String(e)}`
                });
                rowsSkipped++;
                continue;
            }

            const row = bigIntUnpackr.unpack(plainRowBytes) as any[];
            verifiedRows.push({ sr, row });
        }

        // Phase 2: Sender mutation authorization + write shadow + open table.
        // These checks decide whether the sender was allowed to create/update/delete
        // this mutation. They are not receiver read-authorization checks. Under
        // the current full-snapshot policy, a client holding the group CEK may
        // carry/apply the group snapshot; receiver read filtering is not
        // enforced in this import path.
        const isSystemTable = !!group[1];
        if (verifiedRows.length > 0) {
            const colRows = db.exec({
                sql: `SELECT ColumnName, SqlType, CSharpType, IsPrimaryKey FROM _column_registry WHERE TableName = ? ORDER BY ColumnIndex`,
                bind: [tableName],
                returnValue: 'resultRows',
                rowMode: 'array'
            }) as any[][];

            if (!colRows || colRows.length === 0) {
                throw new Error(`deltaImportEncrypted: no _column_registry entries for table '${tableName}'`);
            }

            const columnNames = colRows.map((r: any[]) => r[0] as string);
            const sqlTypes = colRows.map((r: any[]) => r[1] as string);
            const csharpTypes = colRows.map((r: any[]) => r[2] as string);
            const isDeletedIdx = columnNames.indexOf('IsDeleted');
            const pkColumn = columnNames.find((_, i) => colRows[i][3]) ?? 'Id';
            const pkIdx = columnNames.indexOf(pkColumn);

            const v2ImportHeader: any = {
                7: tableName,
                8: colRows.map((r: any[]) => [r[0], r[1], r[2]]),
                9: pkColumn
            };

            // Sender key comes from the ShadowRowGroup level (batch signature verified above)
            const senderEd25519Hex = senderPubKeyHex;

            // System tables: verify sender IS the admin (only admin may modify
            // Contacts, ShareGroups, ShareTargets). Non-admin senders are rejected
            // entirely — no partial row-level checks.
            if (isSystemTable) {
                const senderIsAdmin = verifySenderIsAdmin(db, senderEd25519Hex);
                if (!senderIsAdmin) {
                    for (const verified of verifiedRows) {
                        const rowId = verified.sr[0];
                        const rowIdHex = rowId instanceof Uint8Array
                            ? bytesToHex(rowId) : String(rowId);
                        errors.push({
                            code: 'PERMISSION_INSERT_DENIED',
                            table: tableName, rowId: rowIdHex, groupId: header.groupContext,
                            message: `Only admin may modify system table ${tableName}`
                        });
                        rowsSkipped++;
                    }
                    return { rowsImported, rowsSkipped, rowsDeleted, errors };
                }
            }

            // Domain tables: resolve sender's role and enforce CRUD permissions.
            const permissions = isSystemTable
                ? null
                : resolveSenderPermissions(db, tableName, senderEd25519Hex, header);

            if (!isSystemTable && permissions === null) {
                for (const verified of verifiedRows) {
                    const rowId = verified.sr[0];
                    const rowIdHex = rowId instanceof Uint8Array
                        ? bytesToHex(rowId) : String(rowId);
                    errors.push({
                        code: 'PERMISSION_SENDER_UNAUTHORIZED',
                        table: tableName, rowId: rowIdHex, groupId: header.groupContext,
                        message: `Sender is not authorized for ${tableName}`
                    });
                    rowsSkipped++;
                }
                return { rowsImported, rowsSkipped, rowsDeleted, errors };
            }

            // Permission check each verified row. Collect approved rows
            // with their shadow data for atomic write.
            const approvedInserts: { sr: any[]; converted: any[] }[] = [];
            const approvedDeletes: { sr: any[]; id: unknown }[] = [];

            for (const verified of verifiedRows) {
                const { sr, row } = verified;
                const rowId = sr[0];
                const isDeleted = isDeletedIdx >= 0 && !!row[isDeletedIdx];
                const rowIdHex = rowId instanceof Uint8Array
                    ? bytesToHex(rowId) : String(rowId);

                if (isDeleted) {
                    if (permissions && permissions.deleteDenied) {
                        errors.push({
                            code: 'PERMISSION_DELETE_DENIED',
                            table: tableName, rowId: rowIdHex, groupId: header.groupContext,
                            message: `Sender role lacks delete permission on ${tableName}`
                        });
                        rowsSkipped++;
                        continue;
                    }
                    approvedDeletes.push({ sr, id: rowId });
                } else {
                    const converted = row.map((val: any, idx: number) =>
                        convertValueForSqlite(val, csharpTypes[idx], sqlTypes[idx]));

                    if (permissions) {
                        const existingRow = db.exec({
                            sql: `SELECT "${pkColumn}" FROM "${tableName}" WHERE "${pkColumn}" = ? LIMIT 1`,
                            bind: [converted[pkIdx]],
                            returnValue: 'resultRows',
                            rowMode: 'array'
                        });
                        const isInsert = !existingRow || existingRow.length === 0;

                        if (isInsert && permissions.insertDenied) {
                            errors.push({
                                code: 'PERMISSION_INSERT_DENIED',
                                table: tableName, rowId: rowIdHex, groupId: header.groupContext,
                                message: `Sender role lacks insert permission on ${tableName}`
                            });
                            rowsSkipped++;
                            continue;
                        }

                        if (!isInsert && permissions.updateDenied) {
                            if (permissions.readwriteColumns.length > 0) {
                                const changedCols = getChangedColumns(
                                    db, tableName, pkColumn, converted[pkIdx],
                                    columnNames, converted);
                                const disallowed = changedCols.filter((c: string) => !permissions.readwriteColumns.includes(c));
                                if (disallowed.length > 0) {
                                    errors.push({
                                        code: 'PERMISSION_UPDATE_DENIED',
                                        table: tableName, rowId: rowIdHex, groupId: header.groupContext,
                                        message: `Sender role may only update [${permissions.readwriteColumns.join(', ')}] but also changed: ${disallowed.join(', ')}`
                                    });
                                    rowsSkipped++;
                                    continue;
                                }
                            } else {
                                errors.push({
                                    code: 'PERMISSION_UPDATE_DENIED',
                                    table: tableName, rowId: rowIdHex, groupId: header.groupContext,
                                    message: `Sender role lacks update permission on ${tableName}`
                                });
                                rowsSkipped++;
                                continue;
                            }
                        }

                        if (!isInsert && !permissions.updateDenied && permissions.readonlyColumns.length > 0) {
                            const colViolations = checkColumnPermissions(
                                db, tableName, pkColumn, converted[pkIdx],
                                columnNames, converted, permissions.readonlyColumns);
                            if (colViolations.length > 0) {
                                errors.push({
                                    code: 'PERMISSION_COLUMN_READONLY',
                                    table: tableName, rowId: rowIdHex, groupId: header.groupContext,
                                    message: `Readonly columns mutated: ${colViolations.join(', ')}`
                                });
                                rowsSkipped++;
                                continue;
                            }
                        }
                    }

                    approvedInserts.push({ sr, converted });
                }
            }

            // Write shadow rows only for sender-approved mutations. Sender-denied
            // mutations get no shadow entry. This is unrelated to receiver read
            // permission; CanRead is not a shadow-retention/materialization rule today.
            // Wire format has 6 elements per row; DB table has SenderPublicKey + EnvelopeSignature
            // columns — set sender from group level, signature empty (batch sig is at group level).
            if (approvedInserts.length > 0 || approvedDeletes.length > 0) {
                const emptySignature = new Uint8Array(0);
                const shadowSql =
                    `INSERT OR REPLACE INTO "${cryptoTableName}" ` +
                    `(Id, SharingScope, SharingId, EncryptedRow, Nonce, KeyVersion, SenderPublicKey, EnvelopeSignature) ` +
                    `VALUES (?, ?, ?, ?, ?, ?, ?, ?)`;

                db.exec('BEGIN');
                try {
                    const shadowStmt = db.prepare(shadowSql);
                    for (const { sr } of approvedInserts) {
                        shadowStmt.bind([sr[0], sr[1], sr[2], sr[3], sr[4], sr[5], senderEd25519Hex, emptySignature]);
                        shadowStmt.step();
                        shadowStmt.reset();
                    }
                    for (const { sr } of approvedDeletes) {
                        shadowStmt.bind([sr[0], sr[1], sr[2], sr[3], sr[4], sr[5], senderEd25519Hex, emptySignature]);
                        shadowStmt.step();
                        shadowStmt.reset();
                    }
                    shadowStmt.finalize();
                    db.exec('COMMIT');
                } catch (e) {
                    try { db.exec('ROLLBACK'); } catch { /* ignore */ }
                    logger.error(MODULE_NAME, `deltaImportEncrypted: shadow upsert failed:`, e);
                    throw e;
                }
            }

            // Delete tombstoned rows from both open + shadow
            if (approvedDeletes.length > 0) {
                const deleteSql = `DELETE FROM "${tableName}" WHERE Id = ?`;
                const deleteShadowSql = `DELETE FROM "${cryptoTableName}" WHERE Id = ?`;
                db.exec('BEGIN');
                try {
                    const deleteStmt = db.prepare(deleteSql);
                    const deleteShadowStmt = db.prepare(deleteShadowSql);
                    for (const { id } of approvedDeletes) {
                        deleteStmt.bind([id]);
                        deleteStmt.step();
                        deleteStmt.reset();
                        deleteShadowStmt.bind([id]);
                        deleteShadowStmt.step();
                        deleteShadowStmt.reset();
                        rowsDeleted++;
                    }
                    deleteStmt.finalize();
                    deleteShadowStmt.finalize();
                    db.exec('COMMIT');
                } catch (e) {
                    try { db.exec('ROLLBACK'); } catch { /* ignore */ }
                    throw e;
                }
            }

            // Insert/update approved rows into open table
            if (approvedInserts.length > 0) {
                const rows = approvedInserts.map(a => a.converted);
                const result = bulkInsertRows(db, v2ImportHeader, rows,
                    3 /* DeltaWins = always overwrite; permission enforcement is the gatekeeper */,
                    'deltaImportEncrypted');
                rowsImported = result.rowsAffected;
            }
        }

    logger.info(MODULE_NAME,
        `✓ applyShadowRowGroup: ${tableName} → ${rowsImported} imported, ${rowsDeleted} deleted, ${rowsSkipped} skipped, ${errors.length} errors`);

    return { rowsImported, rowsSkipped, rowsDeleted, errors };
}

/**
 * Encrypted delta import. Consumes a packed `DeltaEnvelope` (multi-group,
 * multi-table), verifies the outer signature, staggers groups so system
 * tables land before domain tables (permission lookups on the receiver
 * read Contacts/ShareGroups/ShareTargets that the system groups just
 * wrote), then delegates each group to `applyShadowRowGroup`.
 *
 * Wire format consumed — DeltaEnvelope:
 *   [version=1, senderEd25519PubHex, outerSignature, groups[]]
 * Outer signature is verified via `verifyBatch([pack(groups)], [empty])`
 * — the identical byte layout the exporter signs.
 */
export async function deltaImportEncrypted(
    dbName: string, headerBytes: Uint8Array, envelopeBytes: Uint8Array, metadata: any
) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    const header = parseV2CryptoHeader(headerBytes);
    const errors: ImportErrorRow[] = [];
    let cek: Uint8Array | null = null;

    const packReport = (imported: number, skipped: number, deleted: number = 0) => ({
        rawBinary: true,
        data: pack([imported, skipped, errors.map(e => [
            importErrorCodeToInt(e.code), e.table, e.rowId, e.groupId, e.message
        ]), deleted])
    });

    try {
        try {
            cek = await unwrapCekFromHeader(header);
        } catch (e) {
            errors.push({
                code: 'TAMPER_CEK_UNWRAP_FAILED',
                table: 'envelope', rowId: '', groupId: header.groupContext,
                message: `CEK unwrap failed: ${e instanceof Error ? e.message : String(e)}`
            });
            return packReport(0, 0);
        }

        // Unpack envelope: [version, senderEd25519PubHex, outerSignature, groups]
        const envelope = unpack(envelopeBytes) as unknown[];
        if (!Array.isArray(envelope) || envelope.length < 4) {
            throw new Error('deltaImportEncrypted: invalid DeltaEnvelope (expected 4-element array)');
        }
        const version = envelope[0] as number;
        if (version !== 1) {
            throw new Error(`deltaImportEncrypted: unsupported envelope version ${version}`);
        }
        const senderPubHex = envelope[1] as string;
        const outerSignature = envelope[2] as Uint8Array;
        const groups = envelope[3] as unknown[][];

        if (!Array.isArray(groups)) {
            throw new Error('deltaImportEncrypted: DeltaEnvelope.groups is not an array');
        }

        // Verify outer signature using the identical byte layout the exporter
        // signed: signBatch([pack(groups)], [zero-length nonce], privKey).
        const senderPubBytes = hexToBytes(senderPubHex);
        const packedGroups = pack(groups);
        if (!verifyBatch([packedGroups], [new Uint8Array(0)], outerSignature, senderPubBytes)) {
            errors.push({
                code: 'TAMPER_SIGNATURE_INVALID',
                table: 'envelope', rowId: '', groupId: '',
                message: 'Outer envelope signature invalid — entire delta rejected'
            });
            return packReport(0, 0);
        }

        // Stagger: system tables first so permission-lookup chain resolves.
        const indexedGroups = groups.map((g: any, i) => ({
            g, idx: i,
            isSystem: !!(Array.isArray(g) && g[1]),
            tableName: (Array.isArray(g) ? (g[0] as string) : '')
        })).sort((a, b) => {
            const aSys = a.isSystem ? 0 : 1;
            const bSys = b.isSystem ? 0 : 1;
            return aSys !== bSys ? aSys - bSys : a.tableName.localeCompare(b.tableName);
        });

        let totalImported = 0;
        let totalSkipped = 0;
        let totalDeleted = 0;

        for (const { g } of indexedGroups) {
            const result = await applyShadowRowGroup(db, g as unknown[], header, cek);
            totalImported += result.rowsImported;
            totalSkipped += result.rowsSkipped;
            totalDeleted += result.rowsDeleted;
            if (result.errors.length > 0) {
                errors.push(...result.errors);
            }
        }

        logger.info(MODULE_NAME,
            `✓ deltaImportEncrypted: envelope → ${indexedGroups.length} groups, ${totalImported} imported, ${totalDeleted} deleted, ${totalSkipped} skipped, ${errors.length} errors`);

        return packReport(totalImported, totalSkipped, totalDeleted);
    } finally {
        if (cek) { clearBytes(cek); }
    }
}

// ============================================================================
// Key rotation
// ============================================================================

async function bulkRotateKey(dbName: string, keyPayload: Uint8Array, metadata: any, oldAad?: Uint8Array) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    const sharingId = metadata.sharingId as string | undefined;
    if (!sharingId) {
        throw new Error('bulkRotateKey: metadata.sharingId is required');
    }

    if (keyPayload.length < 64) {
        throw new Error(`bulkRotateKey: keyPayload must be 64 bytes (oldKey+newKey), got ${keyPayload.length}`);
    }

    const oldKeyBytes = new Uint8Array(32);
    const newKeyBytes = new Uint8Array(32);
    oldKeyBytes.set(keyPayload.slice(0, 32));
    newKeyBytes.set(keyPayload.slice(32, 64));

    const newKeyVersion = metadata.newKeyVersion as number | undefined;

    try {
        // Walk every crypto shadow table — a sharing group's rows may span
        // multiple tables (e.g. a List plus its Items share the same SharingId
        // via the SharingService FK walk), and a rotate must re-encrypt all
        // of them atomically.
        const tableRows = db.exec({
            sql: `SELECT name FROM sqlite_master WHERE type='table' AND name LIKE '_crypto_%' ORDER BY name`,
            returnValue: 'resultRows',
            rowMode: 'array'
        }) as any[][];

        if (!tableRows || tableRows.length === 0) {
            logger.info(MODULE_NAME, `bulkRotateKey: no _crypto_* shadow tables found`);
            return { rowsAffected: 0 };
        }

        let totalRowsAffected = 0;

        db.exec('BEGIN');
        try {
            for (const tableRow of tableRows) {
                const cryptoTable = tableRow[0] as string;

                const rows = db.exec({
                    sql: `SELECT Id, EncryptedRow, Nonce FROM "${cryptoTable}" WHERE SharingId = ?`,
                    bind: [sharingId],
                    returnValue: 'resultRows',
                    rowMode: 'array'
                }) as any[][];

                if (!rows || rows.length === 0) {
                    continue;
                }

                const updateSql = newKeyVersion !== undefined
                    ? `UPDATE "${cryptoTable}" SET EncryptedRow = ?, Nonce = ?, KeyVersion = ?, EnvelopeSignature = ? WHERE Id = ?`
                    : `UPDATE "${cryptoTable}" SET EncryptedRow = ?, Nonce = ?, EnvelopeSignature = ? WHERE Id = ?`;
                const stmt = db.prepare(updateSql);

                try {
                    for (let i = 0; i < rows.length; i++) {
                        const row = rows[i] as any[];
                        const id = row[0];
                        const oldCipher = row[1] as Uint8Array;
                        const oldNonce = row[2] as Uint8Array;

                        // Decrypt with old key + AAD (matches what encryptAesGcm used during export)
                        const plaintext = await decryptAesGcm(
                            { ciphertext: oldCipher, nonce: oldNonce },
                            oldKeyBytes,
                            oldAad
                        );

                        // Re-encrypt with new key (no AAD — next export re-encrypts
                        // from the open table with the new group context's AAD)
                        const encrypted = await encryptAesGcm(plaintext, newKeyBytes);

                        const emptySignature = new Uint8Array(0);
                        if (newKeyVersion !== undefined) {
                            stmt.bind([encrypted.ciphertext, encrypted.nonce, newKeyVersion, emptySignature, id]);
                        } else {
                            stmt.bind([encrypted.ciphertext, encrypted.nonce, emptySignature, id]);
                        }
                        stmt.step();
                        stmt.reset();
                        totalRowsAffected++;
                    }
                } finally {
                    stmt.finalize();
                }

                logger.info(MODULE_NAME,
                    `bulkRotateKey: re-encrypted ${rows.length} rows in ${cryptoTable} (SharingId=${sharingId})`);
            }
            db.exec('COMMIT');
        } catch (e) {
            try { db.exec('ROLLBACK'); } catch { /* ignore */ }
            throw e;
        }

        logger.info(MODULE_NAME,
            `✓ bulkRotateKey: ${totalRowsAffected} rows rotated across ${tableRows.length} table(s) for SharingId=${sharingId}`);
        return { rowsAffected: totalRowsAffected };
    } finally {
        oldKeyBytes.fill(0);
        newKeyBytes.fill(0);
    }
}

/**
 * V2 key rotation: unwraps old + new CEKs from two V2CryptoHeaders, then
 * re-encrypts every shadow row across every `_crypto_*` table whose
 * SharingId matches `metadata.sharingId`. All key material stays in the
 * worker.
 *
 * binaryPayload = MessagePack(oldV2CryptoHeader)
 * binaryHeader  = MessagePack(newV2CryptoHeader)
 * metadata: { sharingId (required), newKeyVersion? }
 */
export async function bulkRotateKeyV2(
    dbName: string,
    oldHeaderBytes: Uint8Array,
    newHeaderBytes: Uint8Array,
    metadata: any
) {
    const oldHeader = parseV2CryptoHeader(oldHeaderBytes);
    const newHeader = parseV2CryptoHeader(newHeaderBytes);

    let oldCek: Uint8Array | null = null;
    let newCek: Uint8Array | null = null;

    try {
        try {
            oldCek = await unwrapCekFromHeader(oldHeader);
        } catch (e) {
            throw new Error(`bulkRotateKeyV2: failed to unwrap old CEK: ${e instanceof Error ? e.message : String(e)}`);
        }
        try {
            newCek = await unwrapCekFromHeader(newHeader);
        } catch (e) {
            throw new Error(`bulkRotateKeyV2: failed to unwrap new CEK: ${e instanceof Error ? e.message : String(e)}`);
        }

        // Build the 64-byte key payload and delegate to bulkRotateKey
        const keyPayload = new Uint8Array(64);
        keyPayload.set(oldCek, 0);
        keyPayload.set(newCek, 32);

        // Old AAD matches what export used: groupContext:keyVersion
        const oldAad = buildAad(oldHeader.groupContext, oldHeader.keyVersion);

        return await bulkRotateKey(dbName, keyPayload, metadata, oldAad);
    } finally {
        if (oldCek) { clearBytes(oldCek); }
        if (newCek) { clearBytes(newCek); }
    }
}
