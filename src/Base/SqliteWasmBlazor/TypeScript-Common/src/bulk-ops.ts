// bulk-ops.ts
// MessagePack bulk import — prepared statement loop with conflict resolution.

import { logger } from './sqlite-logger';
import { MODULE_NAME } from './worker-state';
import { convertValueForSqlite } from './type-conversion';

export interface BulkInsertHeader {
    0: string;      // magic "SWBV2"
    1: string;      // schemaHash
    2: string;      // dataType
    3: string | null;// appIdentifier
    4: string;      // exportedAt
    5: number;      // recordCount
    6: number;       // mode: 0=seed, 1=LastWriteWins, 2=LocalWins, 3=DeltaWins
    7: string;      // tableName
    8: string[][];  // columns: [[name, sqlType, csharpType], ...]
    9: string;      // primaryKeyColumn
    [key: number]: any;
}

/**
 * Build SQL INSERT statement from header metadata.
 * conflictStrategy: 0=plain INSERT, 1=LastWriteWins, 2=LocalWins, 3=DeltaWins
 */
export function buildInsertSql(header: BulkInsertHeader, conflictStrategy: number): string {
    const tableName = header[7];
    const columns = header[8];
    const pkColumn = header[9];
    const columnNames = columns.map(c => `"${c[0]}"`);
    const placeholders = columns.map(() => '?').join(', ');

    let sql = `INSERT INTO "${tableName}" (${columnNames.join(', ')}) VALUES (${placeholders})`;

    if (conflictStrategy === 0) {
        // Seed mode: plain INSERT (no conflict handling)
        return sql;
    }

    // Build SET clause for UPDATE (all columns except primary key)
    const nonPkColumns = columns.filter(c => c[0] !== pkColumn);
    const setClause = nonPkColumns
        .map(c => `"${c[0]}" = excluded."${c[0]}"`)
        .join(', ');

    switch (conflictStrategy) {
        case 1: {
            // LastWriteWins: update only if imported is newer
            const tsColumn = columns.find(c => c[0] === 'UpdatedAt');
            const tsName = tsColumn ? tsColumn[0] : 'UpdatedAt';
            sql += ` ON CONFLICT("${pkColumn}") DO UPDATE SET ${setClause} WHERE excluded."${tsName}" > "${tableName}"."${tsName}"`;
            break;
        }
        case 2:
            // LocalWins: only insert new items
            sql += ` ON CONFLICT("${pkColumn}") DO NOTHING`;
            break;
        case 3:
            // DeltaWins: always overwrite
            sql += ` ON CONFLICT("${pkColumn}") DO UPDATE SET ${setClause}`;
            break;
    }

    return sql;
}

/**
 * Core bulk insert: builds SQL from header, converts values, inserts rows in a transaction.
 * Shared by importRows (plain path) and the encrypted import path in crypto-delta.
 */
export function bulkInsertRows(db: any, header: BulkInsertHeader, rows: any[][], conflictStrategy: number, label: string, readonlyColumnsMap?: Record<string, string[]>) {
    const columns = header[8];
    const csharpTypes = columns.map(c => c[2]);
    const sqlTypes = columns.map(c => c[1]);
    const tableName = header[7];
    const pkColumn = header[9];

    // Look up readonly columns for this specific table
    const readonlyColumns = readonlyColumnsMap?.[tableName];

    logger.info(MODULE_NAME, `${label}: ${rows.length} items into "${tableName}", strategy=${conflictStrategy}`);

    const sql = buildInsertSql(header, conflictStrategy);
    logger.debug(MODULE_NAME, `${label} SQL: ${sql}`);

    let rowsAffected = 0;

    db.exec("BEGIN");
    try {
        // Snapshot readonly columns before apply (if validation requested)
        if (readonlyColumns && readonlyColumns.length > 0) {
            const roCols = readonlyColumns.map(c => `"${c}"`).join(', ');
            db.exec(`CREATE TEMP TABLE IF NOT EXISTS _readonlySnapshot AS SELECT "${pkColumn}", ${roCols} FROM "${tableName}" WHERE 0`);
            db.exec(`DELETE FROM _readonlySnapshot`);
            db.exec(`INSERT INTO _readonlySnapshot SELECT "${pkColumn}", ${roCols} FROM "${tableName}"`);
        }

        const stmt = db.prepare(sql);
        try {
            for (let i = 0; i < rows.length; i++) {
                const row = rows[i] as any[];
                const converted = row.map((val: any, idx: number) => convertValueForSqlite(val, csharpTypes[idx], sqlTypes[idx]));
                stmt.bind(converted);
                stmt.step();
                stmt.reset();
                rowsAffected++;
            }
        } finally {
            stmt.finalize();
        }

        // Validate readonly columns weren't mutated AND no new rows inserted
        if (readonlyColumns && readonlyColumns.length > 0) {
            // Check for new rows (not in snapshot = new inserts → rejected)
            const newRowSql = `SELECT t."${pkColumn}" FROM "${tableName}" t LEFT JOIN _readonlySnapshot s ON t."${pkColumn}" = s."${pkColumn}" WHERE s."${pkColumn}" IS NULL LIMIT 1`;
            const newRows = db.exec({ sql: newRowSql, returnValue: 'resultRows', rowMode: 'array' });
            if (newRows && newRows.length > 0) {
                db.exec(`DROP TABLE IF EXISTS _readonlySnapshot`);
                throw new Error(`Readonly column violation: sender cannot insert new rows when readonly columns are enforced`);
            }

            // Check for mutations on existing rows
            const violations: string[] = [];
            for (const col of readonlyColumns) {
                const checkSql = `SELECT s."${pkColumn}" FROM _readonlySnapshot s JOIN "${tableName}" t ON s."${pkColumn}" = t."${pkColumn}" WHERE s."${col}" IS NOT t."${col}" LIMIT 1`;
                const result = db.exec({ sql: checkSql, returnValue: 'resultRows', rowMode: 'array' });
                if (result && result.length > 0) {
                    violations.push(col);
                }
            }
            db.exec(`DROP TABLE IF EXISTS _readonlySnapshot`);

            if (violations.length > 0) {
                throw new Error(`Readonly column violation: ${violations.join(', ')} were mutated by sender`);
            }
        }

        db.exec("COMMIT");
    } catch (error) {
        try {
            db.exec("ROLLBACK");
        } catch {
            // Ignore rollback errors
        }
        logger.error(MODULE_NAME, `${label} failed:`, error);
        throw error;
    }

    logger.info(MODULE_NAME, `✓ ${label}: ${rowsAffected} rows inserted into "${tableName}"`);
    return { rowsAffected };
}
