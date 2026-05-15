// crypto-permissions.ts
// Admin verification + ShareTarget credential check + permission-table
// signature verification + sender role resolution + column-level enforcement.
//
// Extracted from crypto-ops.ts (G3.5b). No behavior change.
//
// The permission-table signature cache is a module-local `let` that survives
// only for the lifetime of the worker (one verification per page load). The
// cache key is implicit: the worker only ever talks to one DB schema.

import { logger, MODULE_NAME } from '@sqlitewasmblazor/worker-common';
import { ed25519Verify, sha256 } from '@sqlitewasmblazor/crypto-core';
import { bytesToHex, hexToBytes } from './crypto-header';

// ============================================================================
// Admin / ShareTarget / Permission-table verification
// ============================================================================

/**
 * Verify the sender of the most recent shadow row is the admin device.
 * Admin's Ed25519 public key is found via: Contacts WHERE IsAdmin = 1.
 * The sender's Ed25519 key (hex) is stored in the shadow table's SenderPublicKey column.
 */
export function verifySenderIsAdmin(db: any, senderEd25519Hex: string): boolean {
    const adminRows = db.exec({
        sql: `SELECT Ed25519PublicKey FROM Contacts WHERE IsAdmin = 1 AND IsDeleted = 0 LIMIT 1`,
        returnValue: 'resultRows',
        rowMode: 'array'
    }) as any[][];

    if (!adminRows || adminRows.length === 0) {
        logger.warn(MODULE_NAME, 'verifySenderIsAdmin: no admin contact found');
        return false;
    }

    const adminEd25519Base64 = adminRows[0][0] as string;
    const adminBytes = Uint8Array.from(atob(adminEd25519Base64), c => c.charCodeAt(0));
    const adminHex = bytesToHex(adminBytes);

    return senderEd25519Hex === adminHex;
}

/**
 * Verify the AdminSignature on a ShareTarget credential (Step 2b).
 * Canonical payload: `memberPublicKeyBase64 | role | groupContext | keyVersion`
 * Verified against GroupAdminEd25519PublicKey on the ShareTarget row.
 */
async function verifyShareTargetCredential(
    memberPublicKeyBase64: string,
    role: number,
    groupContext: string,
    keyVersion: number,
    adminSignature: Uint8Array,
    groupAdminEd25519PublicKeyBase64: string
): Promise<boolean> {
    const canonical = `${memberPublicKeyBase64}|${role}|${groupContext}|${keyVersion}`;
    const canonicalBytes = new TextEncoder().encode(canonical);
    const pubKeyBytes = Uint8Array.from(atob(groupAdminEd25519PublicKeyBase64), c => c.charCodeAt(0));
    return await ed25519Verify(adminSignature, canonicalBytes, pubKeyBytes);
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

// Session cache for permission table signature verification (Step 2d).
// Once verified, the result is cached for the lifetime of the worker.
// Reset on worker restart (page reload).
let permissionTableVerified: boolean | null = null;

/**
 * Verify the permission table signature (Step 2d). Reads PermissionTableSignature
 * row, recomputes the canonical SHA-256 hash over all Permissions rows, verifies
 * the hash matches and the Admin's Ed25519 signature is valid.
 *
 * Canonical format matches C# PermissionTableHash.Compute():
 * Rows sorted by (TableName, Role), each: `TableName|Role|CanInsert|CanRead|CanUpdate|CanDelete|ReadonlyColumns|ReadwriteColumns\n`
 */
async function verifyPermissionTableSignature(db: any): Promise<boolean> {
    if (permissionTableVerified !== null) {
        return permissionTableVerified;
    }

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

    // Canonical hash — must match C# PermissionTableHash.Compute().
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

    const hashBase64 = btoa(Array.from(computedHash).map(b => String.fromCharCode(b)).join(''));
    const messageBytes = new TextEncoder().encode(hashBase64);
    const pubKeyBytes = Uint8Array.from(atob(adminEd25519PubBase64), c => c.charCodeAt(0));

    if (!await ed25519Verify(adminSignature, messageBytes, pubKeyBytes)) {
        logger.warn(MODULE_NAME, 'verifyPermissionTableSignature: Admin signature invalid');
        permissionTableVerified = false;
        return false;
    }

    logger.info(MODULE_NAME, `✓ Permission table verified: ${permRows.length} rows, signature valid`);
    permissionTableVerified = true;
    return true;
}

// ============================================================================
// Sender role resolution + column-level enforcement
// ============================================================================

export interface ParsedPermissions {
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
export async function resolveSenderPermissions(
    db: any, tableName: string,
    senderEd25519Hex: string,
    header: { groupContext: string }
): Promise<ParsedPermissions | null> {
    // Step 1: Ed25519 hex → Contact → X25519PublicKey
    const ed25519Bytes = hexToBytes(senderEd25519Hex);
    const ed25519Base64 = btoa(Array.from(ed25519Bytes).map(b => String.fromCharCode(b)).join(''));

    const contactRows = db.exec({
        sql: `SELECT X25519PublicKey FROM Contacts WHERE Ed25519PublicKey = ? AND IsDeleted = 0 LIMIT 1`,
        bind: [ed25519Base64],
        returnValue: 'resultRows',
        rowMode: 'array'
    }) as any[][];

    if (!contactRows || contactRows.length === 0) {
        logger.warn(MODULE_NAME, `resolveSenderPermissions: sender contact not found for Ed25519 key`);
        return null;
    }

    const senderX25519PubKey = contactRows[0][0] as string;

    // Step 2a: X25519PubKey + ShareGroup → ShareTarget (with credential fields).
    // Filter out soft-deleted rows on both sides of the join — raw SQL bypasses
    // the C# HasQueryFilter so the filter must be expressed here.
    const targetRows = db.exec({
        sql: `SELECT st.Role, st.AdminSignature, st.GroupAdminEd25519PublicKey, st.KeyVersion, sg.GroupContext
              FROM ShareTargets st
              JOIN ShareGroups sg ON st.ShareGroupId = sg.Id
              WHERE st.MemberPublicKey = ? AND sg.GroupContext = ? AND st.KeyVersion = sg.KeyVersion
                AND st.IsDeleted = 0 AND sg.IsDeleted = 0
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

    // Step 2b: verify AdminSignature on the ShareTarget credential.
    // Empty signatures are rejected — every ShareTarget must carry a valid credential.
    if (!adminSignature || adminSignature.length === 0) {
        logger.warn(MODULE_NAME, `resolveSenderPermissions: ShareTarget missing AdminSignature for ${header.groupContext}`);
        return null;
    }

    if (!await verifyShareTargetCredential(
        senderX25519PubKey, senderRole, groupContext, keyVersion,
        adminSignature, groupAdminEd25519PubKey)) {
        logger.warn(MODULE_NAME, `resolveSenderPermissions: ShareTarget AdminSignature invalid for ${header.groupContext}`);
        return null;
    }

    // Step 2c: verify the GroupAdmin who signed this credential is a trusted contact.
    if (!verifyGroupAdminIsTrusted(db, groupAdminEd25519PubKey)) {
        logger.warn(MODULE_NAME, `resolveSenderPermissions: GroupAdmin ${groupAdminEd25519PubKey.substring(0, 12)}… is not a trusted contact`);
        return null;
    }

    // Step 2d: verify permission table integrity before consulting it.
    if (!await verifyPermissionTableSignature(db)) {
        logger.warn(MODULE_NAME, `resolveSenderPermissions: permission table signature invalid — rejecting`);
        return null;
    }

    // Step 4: Role + TableName → fully resolved permission columns.
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
export function getChangedColumns(
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
export function checkColumnPermissions(
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
