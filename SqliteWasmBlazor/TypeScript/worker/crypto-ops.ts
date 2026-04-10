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

        const groupArray: unknown[] = [tableName, isSystemTable, shadowRowArrays];
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

        const aad = buildAad(header.groupContext, header.keyVersion);

        // Phase 1: Upsert shadow rows + verify + decrypt
        const arrivedRows: { id: unknown; row: any[] }[] = [];

        const shadowSql =
            `INSERT OR REPLACE INTO "${cryptoTableName}" ` +
            `(Id, SharingScope, SharingId, EncryptedRow, Nonce, KeyVersion, SenderPublicKey, EnvelopeSignature) ` +
            `VALUES (?, ?, ?, ?, ?, ?, ?, ?)`;

        db.exec('BEGIN');
        try {
            const stmt = db.prepare(shadowSql);

            for (let i = 0; i < shadowRows.length; i++) {
                const sr = shadowRows[i] as any[];
                const rowIdBytes = sr[0] as Uint8Array;
                const rowScope = sr[1] as number;
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

                stmt.bind([sr[0], rowScope, rowSharingId, rowCiphertext, rowNonce,
                    rowKeyVersion, rowSenderPubKey, rowSig]);
                stmt.step();
                stmt.reset();

                arrivedRows.push({ id: sr[0], row });
            }
            stmt.finalize();
            db.exec('COMMIT');
        } catch (e) {
            try { db.exec('ROLLBACK'); } catch { /* ignore */ }
            logger.error(MODULE_NAME, `bulkImportEncryptedV2: shadow upsert failed:`, e);
            throw e;
        }

        // Phase 2: Apply decrypted rows to open table
        if (arrivedRows.length > 0) {
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

            const v2ImportHeader: any = {
                7: tableName,
                8: colRows.map((r: any[]) => [r[0], r[1], r[2]]),
                9: columnNames.find((_, i) => colRows[i][3]) ?? 'Id'
            };

            const rowsToInsert: any[][] = [];
            const idsToDelete: unknown[] = [];

            for (const arrived of arrivedRows) {
                const isDeleted = isDeletedIdx >= 0 && !!arrived.row[isDeletedIdx];
                if (isDeleted) {
                    idsToDelete.push(arrived.id);
                } else {
                    const converted = arrived.row.map((val: any, idx: number) =>
                        convertValueForSqlite(val, csharpTypes[idx], sqlTypes[idx]));
                    rowsToInsert.push(converted);
                }
            }

            if (idsToDelete.length > 0) {
                const deleteSql = `DELETE FROM "${tableName}" WHERE Id = ?`;
                const deleteShadowSql = `DELETE FROM "${cryptoTableName}" WHERE Id = ?`;
                db.exec('BEGIN');
                try {
                    const deleteStmt = db.prepare(deleteSql);
                    const deleteShadowStmt = db.prepare(deleteShadowSql);
                    for (const id of idsToDelete) {
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

            if (rowsToInsert.length > 0) {
                const result = bulkInsertRows(db, v2ImportHeader, rowsToInsert,
                    2 /* LocalWins = INSERT ON CONFLICT DO NOTHING */,
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

export async function bulkRotateKey(dbName: string, keyPayload: Uint8Array, metadata: any) {
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
