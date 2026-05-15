// crypto-delta.ts
// Encrypted delta export/import/rotate — crypto-core integration.
// Shadow rows ARE the wire format (no outer envelope encryption).
//
// Surviving body of the original crypto-ops.ts after G3.5a/b lifted out
// the header/CEK lifecycle (`crypto-header.ts`) and the permission /
// credential-chain verification (`crypto-permissions.ts`). Renamed in
// G3.5c. No behavior change.
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
// Layer 3 — CEK wrapped via ECDH + HKDF (in CryptoHeader)
//   Protects: group membership proof. Only valid group members can unwrap
//   the CEK. Revoked members don't receive the new wrapped CEK.
//   Cost: O(1) per export/import (one ECDH + HKDF + AES-GCM unwrap).
//
// Wire format: ShadowRowGroup
//   [tableName, isSystemTable, rows[], schemaHash, batchSignature, senderPublicKeyHex]
//   Each row: [Id, SharingScope, SharingId, EncryptedRow, Nonce, KeyVersion]
//

import { logger } from '@sqlitewasmblazor/worker-common';
import { pack, unpack } from 'msgpackr';
import {
    encryptAesGcm, decryptAesGcm,
    signBatch, verifyBatch,
    clearBytes
} from '@sqlitewasmblazor/crypto-core';
import {
    CryptoHeader,
    parseCryptoHeader, clearCryptoHeader,
    unwrapCekFromHeader, buildAad,
    bytesToHex, hexToBytes,
    computeColumnRegistryHash
} from './crypto-header';
import {
    verifySenderIsAdmin,
    resolveSenderPermissions,
    getChangedColumns,
    checkColumnPermissions
} from './crypto-permissions';
import { openDatabases, sqlite3, bigIntUnpackr, MODULE_NAME, convertValueForSqlite, convertValueFromSqlite, bulkInsertRows } from '@sqlitewasmblazor/worker-common';

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
        case 'PERMISSION_SENDER_UNAUTHORIZED': return 14;
        case 'UNKNOWN_GROUP': return 20;
        default: return 99;
    }
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
    cryptoHeader: CryptoHeader,
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
    const batchSignature = await signBatch(batchCiphertexts, batchNonces, cryptoHeader.clientEd25519PrivateKey);
    const schemaHash = computeColumnRegistryHash(db, tableName);

    logger.info(MODULE_NAME,
        `✓ deltaExportEncrypted: ${tableName} → ${shadowRowArrays.length} rows`);

    // Wire format: [tableName, isSystemTable, rows, schemaHash, batchSignature, senderPublicKeyHex].
    // The isSystemTable slot is sender-advisory only — receivers MUST derive
    // system status from their own local CryptoHeader.systemTables, never from
    // this slot (see applyShadowRowGroup and the import sort).
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

    const cryptoHeader = parseCryptoHeader(headerBytes);
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
        const outerSignature = await signBatch(
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
        clearCryptoHeader(cryptoHeader);
        clearBytes(headerBytes);
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
    header: CryptoHeader,
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

        if (!await verifyBatch(ciphertexts, nonces, batchSignature, senderPubKeyBytes)) {
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
        //
        // System status is derived from the receiver's local trusted CryptoHeader
        // (header.systemTables), NOT from the wire tuple — a malicious member
        // who holds the group CEK could otherwise flip group[1] and bypass the
        // admin-only gate on Contacts/ShareGroups/ShareTargets. group[1] on the
        // wire is sender-advisory only and ignored here.
        const isSystemTable = header.systemTables.includes(tableName);
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
                : await resolveSenderPermissions(db, tableName, senderEd25519Hex, header);

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

    const header = parseCryptoHeader(headerBytes);
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
        if (!await verifyBatch([packedGroups], [new Uint8Array(0)], outerSignature, senderPubBytes)) {
            errors.push({
                code: 'TAMPER_SIGNATURE_INVALID',
                table: 'envelope', rowId: '', groupId: '',
                message: 'Outer envelope signature invalid — entire delta rejected'
            });
            return packReport(0, 0);
        }

        // Stagger: system tables first so permission-lookup chain resolves.
        // System status is derived from the receiver's local trusted CryptoHeader
        // (header.systemTables), NOT from the wire tuple's group[1] bit.
        const systemTableSet = new Set(header.systemTables);
        const indexedGroups = groups.map((g: any, i) => {
            const tableName = Array.isArray(g) ? (g[0] as string) : '';
            return { g, idx: i, isSystem: systemTableSet.has(tableName), tableName };
        }).sort((a, b) => {
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
        clearCryptoHeader(header);
        clearBytes(headerBytes);
    }
}

// ============================================================================
// Key rotation
// ============================================================================

async function bulkRotateKeyCore(dbName: string, keyPayload: Uint8Array, metadata: any, oldAad?: Uint8Array) {
    const db = openDatabases.get(dbName);
    if (!db) {
        throw new Error(`Database ${dbName} not open`);
    }

    const sharingId = metadata.sharingId as string | undefined;
    if (!sharingId) {
        throw new Error('bulkRotateKeyCore: metadata.sharingId is required');
    }

    if (keyPayload.length < 64) {
        throw new Error(`bulkRotateKeyCore: keyPayload must be 64 bytes (oldKey+newKey), got ${keyPayload.length}`);
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
            logger.info(MODULE_NAME, `bulkRotateKeyCore: no _crypto_* shadow tables found`);
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
                    `bulkRotateKeyCore: re-encrypted ${rows.length} rows in ${cryptoTable} (SharingId=${sharingId})`);
            }
            db.exec('COMMIT');
        } catch (e) {
            try { db.exec('ROLLBACK'); } catch { /* ignore */ }
            throw e;
        }

        logger.info(MODULE_NAME,
            `✓ bulkRotateKeyCore: ${totalRowsAffected} rows rotated across ${tableRows.length} table(s) for SharingId=${sharingId}`);
        return { rowsAffected: totalRowsAffected };
    } finally {
        oldKeyBytes.fill(0);
        newKeyBytes.fill(0);
    }
}

/**
 * Key rotation: unwraps old + new CEKs from two CryptoHeaders, then
 * re-encrypts every shadow row across every `_crypto_*` table whose
 * SharingId matches `metadata.sharingId`. All key material stays in the
 * worker.
 *
 * binaryPayload = MessagePack(oldCryptoHeader)
 * binaryHeader  = MessagePack(newCryptoHeader)
 * metadata: { sharingId (required), newKeyVersion? }
 */
export async function bulkRotateKey(
    dbName: string,
    oldHeaderBytes: Uint8Array,
    newHeaderBytes: Uint8Array,
    metadata: any
) {
    const oldHeader = parseCryptoHeader(oldHeaderBytes);
    const newHeader = parseCryptoHeader(newHeaderBytes);

    let oldCek: Uint8Array | null = null;
    let newCek: Uint8Array | null = null;

    try {
        try {
            oldCek = await unwrapCekFromHeader(oldHeader);
        } catch (e) {
            throw new Error(`bulkRotateKey: failed to unwrap old CEK: ${e instanceof Error ? e.message : String(e)}`);
        }
        try {
            newCek = await unwrapCekFromHeader(newHeader);
        } catch (e) {
            throw new Error(`bulkRotateKey: failed to unwrap new CEK: ${e instanceof Error ? e.message : String(e)}`);
        }

        // Build the 64-byte key payload and delegate to bulkRotateKeyCore.
        const keyPayload = new Uint8Array(64);
        keyPayload.set(oldCek, 0);
        keyPayload.set(newCek, 32);

        // Old AAD matches what export used: groupContext:keyVersion
        const oldAad = buildAad(oldHeader.groupContext, oldHeader.keyVersion);

        return await bulkRotateKeyCore(dbName, keyPayload, metadata, oldAad);
    } finally {
        if (oldCek) { clearBytes(oldCek); }
        if (newCek) { clearBytes(newCek); }
        clearCryptoHeader(oldHeader);
        clearCryptoHeader(newHeader);
        clearBytes(oldHeaderBytes);
        clearBytes(newHeaderBytes);
    }
}
