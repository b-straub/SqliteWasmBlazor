// ef-core-functions.ts
// EF Core SQLite functions for decimal support
//
// Aggregate functions (ef_sum, ef_avg, ef_max, ef_min) are replaced by native SQLite
// via SQL preprocessing in SqliteWasmCommand.cs for better performance.
//
// All other functions are implemented here:
// - Arithmetic: ef_add, ef_multiply, ef_divide, ef_mod, ef_negate (return string for decimal precision)
// - Comparison: ef_compare (returns number: -1, 0, 1 for TEXT-stored decimal comparison)
// - Pattern: regexp (returns number: 0 or 1, SQLite has no built-in REGEXP)
// - Collation: EF_DECIMAL (for proper decimal ORDER BY)
//
// Return Type Pattern:
// - Arithmetic functions return STRING to preserve decimal precision when stored back to database
// - Non-arithmetic functions return NUMBER as appropriate for their purpose (comparison result, boolean)

import type { Database } from '@sqlite.org/sqlite-wasm';
import { logger } from './sqlite-logger';

const MODULE_NAME = 'EF Core Functions';

/**
 * Register EF Core functions for SQLite WASM.
 * Note: Aggregate functions (sum, avg, max, min) are replaced by native SQLite
 * via SQL preprocessing and don't need to be registered here.
 *
 * @param db - The SQLite database instance
 * @param sqlite3Module - The sqlite3 module with capi and wasm utilities (needed for collations)
 */
export function registerEFCoreFunctions(db: Database, sqlite3Module: any): void {
    try {
        logger.debug(MODULE_NAME, 'Registering EF Core functions...');

        // Arithmetic functions for decimal operations
        registerArithmeticFunctions(db);

        // ef_compare for numeric decimal comparison
        registerCompareFunction(db);

        // regexp for REGEXP operator support
        registerRegexpFunction(db);

        // EF_DECIMAL collation for proper decimal sorting
        registerCollations(db, sqlite3Module);

        logger.info(MODULE_NAME, 'EF Core functions registered successfully');
    } catch (error) {
        logger.error(MODULE_NAME, 'Failed to register EF Core functions:', error);
        throw error;
    }
}

/**
 * Register arithmetic functions for decimal operations.
 * These handle proper decimal arithmetic for values stored as TEXT in SQLite.
 *
 * Implementation details:
 * - Parse TEXT decimals to JavaScript numbers for calculation
 * - Perform arithmetic using native JavaScript operators
 * - Convert results back to STRING via .toString() to preserve decimal precision
 * - String return type ensures proper round-tripping when stored back to database
 */
function registerArithmeticFunctions(db: Database): void {

    db.createFunction({
        name: 'ef_add',
        xFunc: (ctxPtr: number, left: any, right: any): string | null => {
            if (left === null || right === null) {
                return null;
            }
            const leftNum = typeof left === 'string' ? parseFloat(left) : left;
            const rightNum = typeof right === 'string' ? parseFloat(right) : right;
            return (leftNum + rightNum).toString();
        },
        arity: 2,
        deterministic: true
    });

    db.createFunction({
        name: 'ef_multiply',
        xFunc: (ctxPtr: number, left: any, right: any): string | null => {
            if (left === null || right === null) {
                return null;
            }
            const leftNum = typeof left === 'string' ? parseFloat(left) : left;
            const rightNum = typeof right === 'string' ? parseFloat(right) : right;
            return (leftNum * rightNum).toString();
        },
        arity: 2,
        deterministic: true
    });

    db.createFunction({
        name: 'ef_divide',
        xFunc: (ctxPtr: number, dividend: any, divisor: any): string | null => {
            if (dividend === null || divisor === null) {
                return null;
            }
            const dividendNum = typeof dividend === 'string' ? parseFloat(dividend) : dividend;
            const divisorNum = typeof divisor === 'string' ? parseFloat(divisor) : divisor;
            if (divisorNum === 0) {
                return null;
            }
            return (dividendNum / divisorNum).toString();
        },
        arity: 2,
        deterministic: true
    });

    db.createFunction({
        name: 'ef_mod',
        xFunc: (ctxPtr: number, dividend: any, divisor: any): string | null => {
            if (dividend === null || divisor === null) {
                return null;
            }
            const dividendNum = typeof dividend === 'string' ? parseFloat(dividend) : dividend;
            const divisorNum = typeof divisor === 'string' ? parseFloat(divisor) : divisor;
            if (divisorNum === 0) {
                return null;
            }
            return (dividendNum % divisorNum).toString();
        },
        arity: 2,
        deterministic: true
    });

    db.createFunction({
        name: 'ef_negate',
        xFunc: (ctxPtr: number, value: any): string | null => {
            if (value === null) {
                return null;
            }
            const valueNum = typeof value === 'string' ? parseFloat(value) : value;
            return (-valueNum).toString();
        },
        arity: 1,
        deterministic: true
    });

    logger.debug(MODULE_NAME, 'Registered 5 arithmetic functions');
}

/**
 * Register ef_compare function for proper numeric comparison of decimals stored as TEXT.
 * Required because SQLite's native comparison operators perform lexicographic comparison on TEXT.
 */
function registerCompareFunction(db: Database): void {
    db.createFunction({
        name: 'ef_compare',
        xFunc: (ctxPtr: number, left: any, right: any): number | null => {
            if (left === null || right === null) {
                return null;
            }
            // Convert to numbers (decimals are stored as TEXT in SQLite)
            const leftNum = typeof left === 'string' ? parseFloat(left) : left;
            const rightNum = typeof right === 'string' ? parseFloat(right) : right;

            if (leftNum < rightNum) {
                return -1;
            }
            if (leftNum > rightNum) {
                return 1;
            }
            return 0;
        },
        arity: 2,
        deterministic: true
    });

    logger.debug(MODULE_NAME, 'Registered ef_compare function');
}

/**
 * Register regexp function for REGEXP operator support.
 * SQLite provides REGEXP operator syntax but no built-in implementation.
 */
function registerRegexpFunction(db: Database): void {
    db.createFunction({
        name: 'regexp',
        xFunc: (ctxPtr: number, pattern: string | null, value: string | null): number | null => {
            if (pattern === null || value === null) {
                return null;
            }
            try {
                const regex = new RegExp(pattern);
                return regex.test(value) ? 1 : 0;
            } catch (error) {
                logger.warn(MODULE_NAME, `Invalid regex pattern: ${pattern}`, error);
                return null;
            }
        },
        arity: 2,
        deterministic: true
    });

    logger.debug(MODULE_NAME, 'Registered regexp function');
}

/**
 * Register EF_DECIMAL collation for proper decimal string sorting
 * Uses sqlite3_create_collation_v2 from the C API
 */
function registerCollations(db: Database, sqlite3: any): void {
    if (!sqlite3 || !sqlite3.capi) {
        logger.error(MODULE_NAME, 'sqlite3.capi not available for collation registration');
        return;
    }

    try {
        // Register EF_DECIMAL collation using the C API
        // The comparison function receives string pointers and lengths from WASM memory
        const rc = sqlite3.capi.sqlite3_create_collation_v2(
            db.pointer,  // Database pointer
            'EF_DECIMAL',  // Collation name
            sqlite3.capi.SQLITE_UTF8,  // Text encoding
            null,  // pArg (user data pointer - not needed)
            (pArg: any, len1: number, ptr1: number, len2: number, ptr2: number): number => {
                try {
                    // Read the two decimal strings from WASM memory
                    const heap = sqlite3.wasm.heap8u();
                    const bytes1 = heap.subarray(ptr1, ptr1 + len1);
                    const bytes2 = heap.subarray(ptr2, ptr2 + len2);

                    const str1 = new TextDecoder().decode(bytes1);
                    const str2 = new TextDecoder().decode(bytes2);

                    // Parse as floating point numbers
                    const num1 = parseFloat(str1);
                    const num2 = parseFloat(str2);

                    // Handle NaN cases
                    if (isNaN(num1) || isNaN(num2)) {
                        return 0;  // Treat as equal
                    }

                    // Return comparison result
                    if (num1 < num2) {
                        return -1;
                    }
                    if (num1 > num2) {
                        return 1;
                    }
                    return 0;
                } catch (error) {
                    logger.error(MODULE_NAME, 'Error in EF_DECIMAL collation callback:', error);
                    return 0;
                }
            },
            null  // xDestroy callback (not needed)
        );

        if (rc === sqlite3.capi.SQLITE_OK) {
            logger.info(MODULE_NAME, 'Registered EF_DECIMAL collation successfully');
        } else {
            logger.error(MODULE_NAME, `Failed to register EF_DECIMAL collation: rc=${rc}`);
        }
    } catch (error) {
        logger.error(MODULE_NAME, 'Exception during collation registration:', error);
    }
}
