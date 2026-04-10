// crypto-ops.ts
// V2 encrypted export/import/rotate — crypto-core integration.
// Shadow rows ARE the wire format (no outer envelope encryption).
// Three tamper detection layers per GroupEncryption Persistence PDF.

import { logger } from './sqlite-logger';
import { pack, unpack } from 'msgpackr';
import {
    deriveWrappingKey, unwrapContentKey,
    encryptAesGcm, decryptAesGcm,
    ed25519Sign, ed25519Verify,
    clearBytes,
    type SymmetricEncryptedData
} from '@blazorprf/crypto-core';
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

async function buildCanonicalEnvelope(
    rowIdBytes: Uint8Array, sharingId: string, keyVersion: number,
    senderPubKey: Uint8Array, ciphertext: Uint8Array
): Promise<Uint8Array> {
    const { sha256 } = await import('@noble/hashes/sha256');
    const rowIdHex = bytesToHex(rowIdBytes);
    const senderHex = bytesToHex(senderPubKey);
    const ctHash = bytesToHex(sha256(ciphertext));
    const canonical = `${rowIdHex}|${sharingId}|${keyVersion}|${senderHex}|${ctHash}`;
    return new TextEncoder().encode(canonical);
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
 * Returns null if the sender's role can't be determined (falls back to no enforcement).
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

    // Step 3: X25519PubKey + ShareGroup → ShareTarget.Role
    const targetRows = db.exec({
        sql: `SELECT st.Role FROM ShareTargets st
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

export async function bulkExportEncryptedV2(dbName: string, headerBytes: Uint8Array, metadata: any) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    const tableName = metadata.tableName as string;
    const cryptoTableName = `_crypto_${tableName}`;
    const cryptoHeader = parseV2CryptoHeader(headerBytes);
    let cek: Uint8Array | null = null;

    try {
        cek = await unwrapCekFromHeader(cryptoHeader);

        const colRows = db.exec({
            sql: `SELECT ColumnName, SqlType, CSharpType, IsPrimaryKey FROM _column_registry WHERE TableName = ? ORDER BY ColumnIndex`,
            bind: [tableName],
            returnValue: 'resultRows',
            rowMode: 'array'
        }) as any[][];

        if (!colRows || colRows.length === 0) {
            throw new Error(`bulkExportEncryptedV2: no _column_registry entries for table '${tableName}'`);
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
                `bulkExportEncryptedV2: ${tableName} is not a SyncableEntity (missing Id/SharingScope/SharingId)`);
        }

        const tableCheck = db.exec({
            sql: `SELECT name FROM sqlite_master WHERE type='table' AND name=?`,
            bind: [cryptoTableName],
            returnValue: 'resultRows',
            rowMode: 'array'
        });
        if (!tableCheck || tableCheck.length === 0) {
            throw new Error(`bulkExportEncryptedV2: shadow table ${cryptoTableName} not found`);
        }

        const selectCols = columnNames.map(c => `"${c}"`).join(', ');
        const selectSql = `SELECT ${selectCols} FROM "${tableName}"`;
        logger.info(MODULE_NAME, `bulkExportEncryptedV2: "${tableName}" — ${selectSql.substring(0, 120)}`);

        const isInt64Col = csharpTypes.map(t => {
            const base = t.endsWith('?') ? t.slice(0, -1) : t;
            return base === 'Int64' || base === 'UInt64';
        });
        const SQLITE_TEXT = sqlite3!.capi.SQLITE_TEXT;

        const rows: any[][] = [];
        const readStmt = db.prepare(selectSql);
        try {
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

        const convertedRows = rows.map(row =>
            row.map((val, idx) => convertValueFromSqlite(val, csharpTypes[idx], sqlTypes[idx])));

        const isSystemTable = cryptoHeader.systemTables.indexOf(tableName) >= 0;
        const aad = buildAad(cryptoHeader.groupContext, cryptoHeader.keyVersion);
        const senderPubKeyHex = bytesToHex(cryptoHeader.clientEd25519PublicKey);

        const shadowSql =
            `INSERT OR REPLACE INTO "${cryptoTableName}" ` +
            `(Id, SharingScope, SharingId, EncryptedRow, Nonce, KeyVersion, SenderPublicKey, EnvelopeSignature) ` +
            `VALUES (?, ?, ?, ?, ?, ?, ?, ?)`;
        const stmt = db.prepare(shadowSql);

        const shadowRowArrays: unknown[][] = [];

        db.exec('BEGIN');
        try {
            for (let i = 0; i < convertedRows.length; i++) {
                const row = convertedRows[i];
                const rowScope = Number(row[scopeIdx]);
                const rowSharingId = String(row[sharingIdIdx]);
                const rowIdBytes = guidToBytes(row[idIdx]);

                const rowBytes = pack(row);
                const encrypted = await encryptAesGcm(rowBytes, cek, aad);

                const envelope = await buildCanonicalEnvelope(
                    rowIdBytes, rowSharingId, cryptoHeader.keyVersion,
                    cryptoHeader.clientEd25519PublicKey, encrypted.ciphertext);
                const sig = ed25519Sign(envelope, cryptoHeader.clientEd25519PrivateKey);

                stmt.bind([
                    row[idIdx], rowScope, rowSharingId,
                    encrypted.ciphertext, encrypted.nonce,
                    cryptoHeader.keyVersion, senderPubKeyHex, sig
                ]);
                stmt.step();
                stmt.reset();

                shadowRowArrays.push([
                    rowIdBytes, rowScope, rowSharingId,
                    encrypted.ciphertext, encrypted.nonce,
                    cryptoHeader.keyVersion, senderPubKeyHex, sig
                ]);
            }
            stmt.finalize();
            db.exec('COMMIT');
        } catch (e) {
            try { stmt.finalize(); } catch { /* ignore */ }
            try { db.exec('ROLLBACK'); } catch { /* ignore */ }
            logger.error(MODULE_NAME, `bulkExportEncryptedV2: shadow upsert failed in ${cryptoTableName}:`, e);
            throw e;
        }

        // Schema hash: deterministic hash of _column_registry for this table.
        // The receiver compares this against its own registry to detect version mismatches.
        const schemaHash = computeColumnRegistryHash(db, tableName);

        const groupArray: unknown[] = [tableName, isSystemTable, shadowRowArrays, schemaHash];
        const packed = pack(groupArray);

        logger.info(MODULE_NAME,
            `✓ bulkExportEncryptedV2: ${tableName} → ${shadowRowArrays.length} rows, ${packed.length} bytes`);
        return { rawBinary: true, data: packed };
    } finally {
        if (cek) { clearBytes(cek); }
    }
}

// ============================================================================
// Encrypted import
// ============================================================================

export async function bulkImportEncryptedV2(dbName: string, headerBytes: Uint8Array, groupBytes: Uint8Array, metadata: any) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    const header = parseV2CryptoHeader(headerBytes);
    const errors: { code: string; table: string; rowId: string; groupId: string; message: string }[] = [];
    let rowsImported = 0;
    let rowsSkipped = 0;
    let rowsDeleted = 0;
    let cek: Uint8Array | null = null;

    try {
        try {
            cek = await unwrapCekFromHeader(header);
        } catch (e) {
            errors.push({
                code: 'TAMPER_CEK_UNWRAP_FAILED',
                table: 'group', rowId: '', groupId: header.groupContext,
                message: `CEK unwrap failed: ${e instanceof Error ? e.message : String(e)}`
            });
            return { rawBinary: true, data: pack([0, 0, errors.map(e => [
                importErrorCodeToInt(e.code), e.table, e.rowId, e.groupId, e.message
            ])]) };
        }

        const group = unpack(groupBytes) as unknown[];
        if (!Array.isArray(group) || group.length < 3) {
            throw new Error('bulkImportEncryptedV2: invalid ShadowRowGroup');
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
                    `bulkImportEncryptedV2: schema mismatch for table '${tableName}' — ` +
                    `sender hash ${senderHash.substring(0, 16)}… ≠ local hash ${localHash.substring(0, 16)}…. ` +
                    `All clients must run the same app version.`);
            }
        }

        const aad = buildAad(header.groupContext, header.keyVersion);

        // Phase 1: Verify signatures + decrypt (NO writes yet).
        // Rows that fail tamper detection are skipped immediately.
        // Approved rows are collected for Phase 2 (permission check + write).
        const verifiedRows: { sr: any[]; row: any[] }[] = [];

        for (let i = 0; i < shadowRows.length; i++) {
            const sr = shadowRows[i] as any[];
            const rowIdBytes = sr[0] as Uint8Array;
            const rowSharingId = sr[2] as string;
            const rowCiphertext = sr[3] as Uint8Array;
            const rowNonce = sr[4] as Uint8Array;
            const rowKeyVersion = sr[5] as number;
            const rowSenderPubKey = sr[6] as string;
            const rowSig = sr[7] as Uint8Array;

            const rowIdHex = bytesToHex(rowIdBytes);

            // Layer 2: verify Ed25519 signature
            try {
                const senderPubKeyBytes = hexToBytes(rowSenderPubKey);
                const envelope = await buildCanonicalEnvelope(
                    rowIdBytes, rowSharingId, rowKeyVersion,
                    senderPubKeyBytes, rowCiphertext);
                if (!ed25519Verify(rowSig, envelope, senderPubKeyBytes)) {
                    errors.push({
                        code: 'TAMPER_SIGNATURE_INVALID',
                        table: tableName, rowId: rowIdHex, groupId: header.groupContext,
                        message: `Ed25519 signature invalid for row ${rowIdHex}`
                    });
                    rowsSkipped++;
                    continue;
                }
            } catch (e) {
                errors.push({
                    code: 'TAMPER_SIGNATURE_INVALID',
                    table: tableName, rowId: rowIdHex, groupId: header.groupContext,
                    message: `Signature verification error: ${e instanceof Error ? e.message : String(e)}`
                });
                rowsSkipped++;
                continue;
            }

            // Layer 1: decrypt with AAD
            let plainRowBytes: Uint8Array;
            try {
                plainRowBytes = await decryptAesGcm({ ciphertext: rowCiphertext, nonce: rowNonce }, cek, aad);
            } catch (e) {
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

        // Phase 2: Permission check + write shadow + open table.
        // Only rows that pass permission checks are written to BOTH tables.
        // Denied rows are rejected entirely — no shadow, no open table.
        const isSystemTable = !!group[1];
        if (verifiedRows.length > 0) {
            const colRows = db.exec({
                sql: `SELECT ColumnName, SqlType, CSharpType, IsPrimaryKey FROM _column_registry WHERE TableName = ? ORDER BY ColumnIndex`,
                bind: [tableName],
                returnValue: 'resultRows',
                rowMode: 'array'
            }) as any[][];

            if (!colRows || colRows.length === 0) {
                throw new Error(`bulkImportEncryptedV2: no _column_registry entries for table '${tableName}'`);
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

            // Extract sender's Ed25519 public key from the first verified row.
            // All rows in a ShadowRowGroup come from the same sender.
            const senderEd25519Hex = verifiedRows[0].sr[6] as string;

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

                    const report = [rowsImported, rowsSkipped, errors.map(e => [
                        importErrorCodeToInt(e.code), e.table, e.rowId, e.groupId, e.message
                    ])];
                    return { rawBinary: true, data: pack(report) };
                }
            }

            // Domain tables: resolve sender's role and enforce CRUD permissions.
            const permissions = isSystemTable
                ? null
                : resolveSenderPermissions(db, tableName, senderEd25519Hex, header);

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

            // Write shadow rows ONLY for approved rows (denied rows get no shadow entry)
            if (approvedInserts.length > 0 || approvedDeletes.length > 0) {
                const shadowSql =
                    `INSERT OR REPLACE INTO "${cryptoTableName}" ` +
                    `(Id, SharingScope, SharingId, EncryptedRow, Nonce, KeyVersion, SenderPublicKey, EnvelopeSignature) ` +
                    `VALUES (?, ?, ?, ?, ?, ?, ?, ?)`;

                db.exec('BEGIN');
                try {
                    const shadowStmt = db.prepare(shadowSql);
                    for (const { sr } of approvedInserts) {
                        shadowStmt.bind([sr[0], sr[1], sr[2], sr[3], sr[4], sr[5], sr[6], sr[7]]);
                        shadowStmt.step();
                        shadowStmt.reset();
                    }
                    for (const { sr } of approvedDeletes) {
                        shadowStmt.bind([sr[0], sr[1], sr[2], sr[3], sr[4], sr[5], sr[6], sr[7]]);
                        shadowStmt.step();
                        shadowStmt.reset();
                    }
                    shadowStmt.finalize();
                    db.exec('COMMIT');
                } catch (e) {
                    try { db.exec('ROLLBACK'); } catch { /* ignore */ }
                    logger.error(MODULE_NAME, `bulkImportEncryptedV2: shadow upsert failed:`, e);
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
                    'bulkImportEncryptedV2');
                rowsImported = result.rowsAffected;
            }
        }

        logger.info(MODULE_NAME,
            `✓ bulkImportEncryptedV2: ${tableName} → ${rowsImported} imported, ${rowsDeleted} deleted, ${rowsSkipped} skipped, ${errors.length} errors`);

        const report = [rowsImported, rowsSkipped, errors.map(e => [
            importErrorCodeToInt(e.code), e.table, e.rowId, e.groupId, e.message
        ])];
        return { rawBinary: true, data: pack(report) };
    } finally {
        if (cek) { clearBytes(cek); }
    }
}

// ============================================================================
// Key rotation
// ============================================================================

async function bulkRotateKey(dbName: string, keyPayload: Uint8Array, metadata: any) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    const tableName = metadata.tableName as string | undefined;
    if (!tableName) {
        throw new Error('bulkRotateKey: metadata.tableName is required');
    }

    if (keyPayload.length < 64) {
        throw new Error(`bulkRotateKey: keyPayload must be 64 bytes (oldKey+newKey), got ${keyPayload.length}`);
    }

    const sharingId = metadata.sharingId as string | undefined;
    const cryptoTable = `_crypto_${tableName}`;

    const oldKeyBytes = new Uint8Array(32);
    const newKeyBytes = new Uint8Array(32);
    oldKeyBytes.set(keyPayload.slice(0, 32));
    newKeyBytes.set(keyPayload.slice(32, 64));

    try {
        const tableCheck = db.exec({
            sql: `SELECT name FROM sqlite_master WHERE type='table' AND name=?`,
            bind: [cryptoTable],
            returnValue: 'resultRows',
            rowMode: 'array'
        });
        if (!tableCheck || tableCheck.length === 0) {
            throw new Error(`bulkRotateKey: crypto shadow table not found: ${cryptoTable}`);
        }

        const oldKey = await crypto.subtle.importKey(
            'raw', oldKeyBytes.buffer, { name: 'AES-GCM' }, false, ['decrypt']);
        const newKey = await crypto.subtle.importKey(
            'raw', newKeyBytes.buffer, { name: 'AES-GCM' }, false, ['encrypt']);

        const selectSql = sharingId !== undefined
            ? `SELECT Id, EncryptedRow, Nonce FROM "${cryptoTable}" WHERE SharingId = ?`
            : `SELECT Id, EncryptedRow, Nonce FROM "${cryptoTable}"`;

        const rows = db.exec({
            sql: selectSql,
            bind: sharingId !== undefined ? [sharingId] : [],
            returnValue: 'resultRows',
            rowMode: 'array'
        });

        if (!rows || rows.length === 0) {
            logger.info(MODULE_NAME, `bulkRotateKey: no rows match in ${cryptoTable}${sharingId !== undefined ? ` for SharingId=${sharingId}` : ''}`);
            return { rowsAffected: 0 };
        }

        const newKeyVersion = metadata.newKeyVersion as number | undefined;
        const updateSql = newKeyVersion !== undefined
            ? `UPDATE "${cryptoTable}" SET EncryptedRow = ?, Nonce = ?, KeyVersion = ?, EnvelopeSignature = ? WHERE Id = ?`
            : `UPDATE "${cryptoTable}" SET EncryptedRow = ?, Nonce = ?, EnvelopeSignature = ? WHERE Id = ?`;
        const stmt = db.prepare(updateSql);

        let rowsAffected = 0;
        db.exec('BEGIN');
        try {
            for (let i = 0; i < rows.length; i++) {
                const row = rows[i] as any[];
                const id = row[0];
                const oldCipher = row[1] as Uint8Array;
                const oldNonce = row[2] as Uint8Array;

                const plaintext = await crypto.subtle.decrypt(
                    { name: 'AES-GCM', iv: oldNonce.buffer as ArrayBuffer },
                    oldKey,
                    oldCipher.buffer as ArrayBuffer
                );

                const newNonce = crypto.getRandomValues(new Uint8Array(12));
                const newCipher = await crypto.subtle.encrypt(
                    { name: 'AES-GCM', iv: newNonce.buffer as ArrayBuffer },
                    newKey,
                    plaintext
                );

                const emptySignature = new Uint8Array(0);
                if (newKeyVersion !== undefined) {
                    stmt.bind([new Uint8Array(newCipher), newNonce, newKeyVersion, emptySignature, id]);
                } else {
                    stmt.bind([new Uint8Array(newCipher), newNonce, emptySignature, id]);
                }
                stmt.step();
                stmt.reset();
                rowsAffected++;
            }
            stmt.finalize();
            db.exec('COMMIT');
        } catch (e) {
            try { stmt.finalize(); } catch { /* ignore */ }
            try { db.exec('ROLLBACK'); } catch { /* ignore */ }
            throw e;
        }

        logger.info(MODULE_NAME, `✓ bulkRotateKey: re-encrypted ${rowsAffected} rows in ${cryptoTable}${sharingId !== undefined ? ` (SharingId=${sharingId})` : ''}`);
        return { rowsAffected };
    } finally {
        oldKeyBytes.fill(0);
        newKeyBytes.fill(0);
    }
}

/**
 * V2 key rotation: unwraps old + new CEKs from two V2CryptoHeaders, then
 * re-encrypts all matching shadow rows. All key material stays in the worker.
 *
 * binaryPayload = MessagePack(oldV2CryptoHeader)
 * binaryHeader  = MessagePack(newV2CryptoHeader)
 * metadata: { tableName, sharingId?, newKeyVersion? }
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
        oldCek = await unwrapCekFromHeader(oldHeader);
        newCek = await unwrapCekFromHeader(newHeader);

        // Build the 64-byte key payload and delegate to bulkRotateKey
        const keyPayload = new Uint8Array(64);
        keyPayload.set(oldCek, 0);
        keyPayload.set(newCek, 32);

        return await bulkRotateKey(dbName, keyPayload, metadata);
    } finally {
        if (oldCek) { clearBytes(oldCek); }
        if (newCek) { clearBytes(newCek); }
    }
}
