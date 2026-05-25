// ef-core-functions.ts
// EF Core SQLite functions for decimal support
//
// Implemented functions:
// - Aggregates: ef_sum, ef_avg, ef_max, ef_min (return string for decimal precision)
// - Arithmetic: ef_add, ef_multiply, ef_divide, ef_mod, ef_negate (return string for decimal precision)
// - Comparison: ef_compare (returns number: -1, 0, 1 for TEXT-stored decimal comparison)
// - Pattern: regexp (returns number: 0 or 1, SQLite has no built-in REGEXP)
// - Collation: EF_DECIMAL (for proper decimal ORDER BY)
//
// Return Type Pattern:
// - Arithmetic functions return STRING to preserve decimal precision when stored back to database
// - Non-arithmetic functions return NUMBER as appropriate for their purpose (comparison result, boolean)

import type { Database, SqlValue } from '@sqlite.org/sqlite-wasm';
import { logger } from './sqlite-logger';

const MODULE_NAME = 'EF Core Functions';
const DIVISION_SCALE = 29;
const UINT64_MASK = (1n << 64n) - 1n;

interface ParsedDecimal {
    coefficient: bigint;
    scale: number;
}

interface DecimalAggregateState {
    count: number;
    sum: ParsedDecimal;
    min: ParsedDecimal | null;
    max: ParsedDecimal | null;
}

function parseDecimal(value: any): ParsedDecimal | null {
    if (value === null || value === undefined) {
        return null;
    }

    const text = String(value).trim();
    const match = /^([+-])?(?:(\d+)(?:\.(\d*))?|\.(\d+))(?:[eE]([+-]?\d+))?$/.exec(text);
    if (!match) {
        return null;
    }

    const sign = match[1] === '-' ? -1n : 1n;
    const whole = match[2] ?? '';
    const fractional = match[3] ?? match[4] ?? '';
    const exponent = match[5] ? Number.parseInt(match[5], 10) : 0;

    let digits = `${whole}${fractional}`.replace(/^0+/, '');
    let scale = fractional.length - exponent;

    if (digits.length === 0) {
        return { coefficient: 0n, scale: 0 };
    }

    if (scale < 0) {
        digits += '0'.repeat(-scale);
        scale = 0;
    }

    return normalizeDecimal({
        coefficient: sign * BigInt(digits),
        scale
    });
}

function normalizeDecimal(value: ParsedDecimal): ParsedDecimal {
    let coefficient = value.coefficient;
    let scale = value.scale;

    if (coefficient === 0n) {
        return { coefficient: 0n, scale: 0 };
    }

    while (scale > 0 && coefficient % 10n === 0n) {
        coefficient /= 10n;
        scale--;
    }

    return { coefficient, scale };
}

function pow10(exponent: number): bigint {
    let result = 1n;
    for (let i = 0; i < exponent; i++) {
        result *= 10n;
    }
    return result;
}

function toScale(value: ParsedDecimal, scale: number): bigint {
    return value.coefficient * pow10(scale - value.scale);
}

function decimalToString(value: ParsedDecimal): string {
    const normalized = normalizeDecimal(value);
    if (normalized.coefficient === 0n) {
        return '0';
    }

    const negative = normalized.coefficient < 0n;
    let digits = (negative ? -normalized.coefficient : normalized.coefficient).toString();

    if (normalized.scale > 0) {
        if (digits.length <= normalized.scale) {
            digits = `${'0'.repeat(normalized.scale - digits.length + 1)}${digits}`;
        }

        const pointIndex = digits.length - normalized.scale;
        digits = `${digits.slice(0, pointIndex)}.${digits.slice(pointIndex)}`;
        digits = digits.replace(/\.?0+$/, '');
    }

    return negative ? `-${digits}` : digits;
}

function compareDecimals(left: ParsedDecimal, right: ParsedDecimal): number {
    const scale = Math.max(left.scale, right.scale);
    const leftScaled = toScale(left, scale);
    const rightScaled = toScale(right, scale);

    if (leftScaled < rightScaled) {
        return -1;
    }
    if (leftScaled > rightScaled) {
        return 1;
    }
    return 0;
}

function binaryDecimalOperation(
    left: any,
    right: any,
    operation: (left: ParsedDecimal, right: ParsedDecimal) => ParsedDecimal | null): string | null {
    const leftDecimal = parseDecimal(left);
    const rightDecimal = parseDecimal(right);
    if (!leftDecimal || !rightDecimal) {
        return null;
    }

    const result = operation(leftDecimal, rightDecimal);
    return result ? decimalToString(result) : null;
}

function addDecimals(left: ParsedDecimal, right: ParsedDecimal): ParsedDecimal {
    const scale = Math.max(left.scale, right.scale);
    return normalizeDecimal({
        coefficient: toScale(left, scale) + toScale(right, scale),
        scale
    });
}

function multiplyDecimals(left: ParsedDecimal, right: ParsedDecimal): ParsedDecimal {
    return normalizeDecimal({
        coefficient: left.coefficient * right.coefficient,
        scale: left.scale + right.scale
    });
}

function divideDecimals(dividend: ParsedDecimal, divisor: ParsedDecimal): ParsedDecimal | null {
    if (divisor.coefficient === 0n) {
        return null;
    }

    const sign = (dividend.coefficient < 0n) === (divisor.coefficient < 0n) ? 1n : -1n;
    const numerator = (dividend.coefficient < 0n ? -dividend.coefficient : dividend.coefficient)
        * pow10(divisor.scale + DIVISION_SCALE);
    const denominator = (divisor.coefficient < 0n ? -divisor.coefficient : divisor.coefficient)
        * pow10(dividend.scale);

    let quotient = numerator / denominator;
    const remainder = numerator % denominator;
    if (remainder * 2n >= denominator) {
        quotient += 1n;
    }

    return normalizeDecimal({
        coefficient: quotient * sign,
        scale: DIVISION_SCALE
    });
}

function modDecimals(dividend: ParsedDecimal, divisor: ParsedDecimal): ParsedDecimal | null {
    if (divisor.coefficient === 0n) {
        return null;
    }

    const scale = Math.max(dividend.scale, divisor.scale);
    return normalizeDecimal({
        coefficient: toScale(dividend, scale) % toScale(divisor, scale),
        scale
    });
}

function createAggregateState(): DecimalAggregateState {
    return {
        count: 0,
        sum: { coefficient: 0n, scale: 0 },
        min: null,
        max: null
    };
}

function getAggregateKey(sqlite3: any, ctxPtr: number, allocate: boolean): number {
    const capi = sqlite3?.capi;
    if (!capi?.sqlite3_js_aggregate_context) {
        throw new Error('sqlite3_js_aggregate_context is not available for decimal aggregate functions.');
    }

    return capi.sqlite3_js_aggregate_context(ctxPtr, allocate ? 1 : 0);
}

function registerDecimalAggregate(
    db: Database,
    sqlite3: any,
    name: string,
    final: (state: DecimalAggregateState) => string | null): void {
    const states = new Map<number, DecimalAggregateState>();

    db.createFunction({
        name,
        xStep: (ctxPtr: number, value: SqlValue): void => {
            const decimal = parseDecimal(value);
            if (!decimal) {
                return;
            }

            const key = getAggregateKey(sqlite3, ctxPtr, true);
            let state = states.get(key);
            if (!state) {
                state = createAggregateState();
                states.set(key, state);
            }

            state.count++;
            state.sum = addDecimals(state.sum, decimal);
            if (state.min === null || compareDecimals(decimal, state.min) < 0) {
                state.min = decimal;
            }
            if (state.max === null || compareDecimals(decimal, state.max) > 0) {
                state.max = decimal;
            }
        },
        xFinal: (ctxPtr: number): string | null => {
            const key = getAggregateKey(sqlite3, ctxPtr, false);
            const state = states.get(key);
            if (key) {
                states.delete(key);
            }

            if (!state || state.count === 0) {
                return null;
            }

            return final(state);
        },
        arity: 1,
        deterministic: true
    });
}

/**
 * Register EF Core functions for SQLite WASM.
 * @param db - The SQLite database instance
 * @param sqlite3Module - The sqlite3 module with capi and wasm utilities (needed for collations)
 */
export function registerEFCoreFunctions(db: Database, sqlite3Module: any): void {
    try {
        logger.debug(MODULE_NAME, 'Registering EF Core functions...');

        // Aggregate functions for decimal operations
        registerAggregateFunctions(db, sqlite3Module);

        // Arithmetic functions for decimal operations
        registerArithmeticFunctions(db);

        // ef_compare for numeric decimal comparison
        registerCompareFunction(db);

        // regexp for REGEXP operator support
        registerRegexpFunction(db);

        // Native SQLite math functions used by EF Core. sqlite-wasm builds can
        // differ in compile-time math extension flags, so register compatible
        // implementations to avoid browser-only query gaps.
        registerMathFunctions(db);

        // SQLite core and optional extension functions can differ between
        // native and wasm builds. Register compatible implementations to avoid
        // browser-only query gaps for common app-visible functions.
        registerCompatibilityFunctions(db);

        // EF_DECIMAL collation for proper decimal sorting
        registerCollations(db, sqlite3Module);

        logger.info(MODULE_NAME, 'EF Core functions registered successfully');
    } catch (error) {
        logger.error(MODULE_NAME, 'Failed to register EF Core functions:', error);
        throw error;
    }
}

function registerAggregateFunctions(db: Database, sqlite3: any): void {
    registerDecimalAggregate(db, sqlite3, 'ef_sum', state => decimalToString(state.sum));
    registerDecimalAggregate(db, sqlite3, 'ef_avg', state => {
        const average = divideDecimals(state.sum, { coefficient: BigInt(state.count), scale: 0 });
        return average ? decimalToString(average) : null;
    });
    registerDecimalAggregate(db, sqlite3, 'ef_min', state => state.min ? decimalToString(state.min) : null);
    registerDecimalAggregate(db, sqlite3, 'ef_max', state => state.max ? decimalToString(state.max) : null);

    logger.debug(MODULE_NAME, 'Registered 4 decimal aggregate functions');
}

/**
 * Register arithmetic functions for decimal operations.
 * These handle proper decimal arithmetic for values stored as TEXT in SQLite.
 *
 * Implementation details:
 * - Parse TEXT decimals to fixed-scale BigInt values for calculation
 * - Perform arithmetic without JavaScript floating-point rounding
 * - Convert results back to canonical decimal strings
 * - String return type ensures proper round-tripping when stored back to database
 */
function registerArithmeticFunctions(db: Database): void {

    db.createFunction({
        name: 'ef_add',
        xFunc: (ctxPtr: number, left: any, right: any): string | null => {
            return binaryDecimalOperation(left, right, addDecimals);
        },
        arity: 2,
        deterministic: true
    });

    db.createFunction({
        name: 'ef_multiply',
        xFunc: (ctxPtr: number, left: any, right: any): string | null => {
            return binaryDecimalOperation(left, right, multiplyDecimals);
        },
        arity: 2,
        deterministic: true
    });

    db.createFunction({
        name: 'ef_divide',
        xFunc: (ctxPtr: number, dividend: any, divisor: any): string | null => {
            return binaryDecimalOperation(dividend, divisor, divideDecimals);
        },
        arity: 2,
        deterministic: true
    });

    db.createFunction({
        name: 'ef_mod',
        xFunc: (ctxPtr: number, dividend: any, divisor: any): string | null => {
            return binaryDecimalOperation(dividend, divisor, modDecimals);
        },
        arity: 2,
        deterministic: true
    });

    db.createFunction({
        name: 'ef_negate',
        xFunc: (ctxPtr: number, value: any): string | null => {
            const decimal = parseDecimal(value);
            if (!decimal) {
                return null;
            }

            return decimalToString({
                coefficient: -decimal.coefficient,
                scale: decimal.scale
            });
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
            const leftDecimal = parseDecimal(left);
            const rightDecimal = parseDecimal(right);
            if (!leftDecimal || !rightDecimal) {
                return null;
            }

            return compareDecimals(leftDecimal, rightDecimal);
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
        xFunc: (ctxPtr: number, ...args: SqlValue[]): SqlValue => {
            const pattern = args[0];
            const value = args[1];
            if (pattern === null || value === null) {
                return null;
            }
            try {
                const regex = new RegExp(String(pattern));
                return regex.test(String(value)) ? 1 : 0;
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

function toNumberOrNull(value: SqlValue): number | null {
    if (value === null || value === undefined) {
        return null;
    }

    const number = Number(value);
    return Number.isNaN(number) ? null : number;
}

function normalizeMathResult(value: number): number | null {
    return Number.isNaN(value) ? null : value;
}

function registerUnaryMathFunction(db: Database, name: string, fn: (value: number) => number): void {
    db.createFunction({
        name,
        xFunc: (ctxPtr: number, value: SqlValue): SqlValue => {
            const number = toNumberOrNull(value);
            return number === null ? null : normalizeMathResult(fn(number));
        },
        arity: 1,
        deterministic: true
    });
}

function registerBinaryMathFunction(db: Database, name: string, fn: (left: number, right: number) => number): void {
    db.createFunction({
        name,
        xFunc: (ctxPtr: number, left: SqlValue, right: SqlValue): SqlValue => {
            const leftNumber = toNumberOrNull(left);
            const rightNumber = toNumberOrNull(right);
            return leftNumber === null || rightNumber === null
                ? null
                : normalizeMathResult(fn(leftNumber, rightNumber));
        },
        arity: 2,
        deterministic: true
    });
}

function registerMathFunctions(db: Database): void {
    const unaryFunctions: Array<[string, (value: number) => number]> = [
        ['acos', Math.acos],
        ['acosh', Math.acosh],
        ['asin', Math.asin],
        ['asinh', Math.asinh],
        ['atan', Math.atan],
        ['atanh', Math.atanh],
        ['ceil', Math.ceil],
        ['ceiling', Math.ceil],
        ['cos', Math.cos],
        ['cosh', Math.cosh],
        ['degrees', value => value * 180 / Math.PI],
        ['exp', Math.exp],
        ['floor', Math.floor],
        ['ln', Math.log],
        ['log', Math.log10],
        ['log2', Math.log2],
        ['log10', Math.log10],
        ['radians', value => value * Math.PI / 180],
        ['sin', Math.sin],
        ['sinh', Math.sinh],
        ['sqrt', Math.sqrt],
        ['tan', Math.tan],
        ['tanh', Math.tanh],
        ['trunc', Math.trunc]
    ];

    for (const [name, fn] of unaryFunctions) {
        registerUnaryMathFunction(db, name, fn);
    }

    registerUnaryMathFunction(db, 'sign', value => {
        const sign = Math.sign(value);
        return Object.is(sign, -0) ? 0 : sign;
    });

    registerBinaryMathFunction(db, 'atan2', Math.atan2);
    registerBinaryMathFunction(db, 'mod', (left, right) => left % right);
    registerBinaryMathFunction(db, 'pow', Math.pow);
    registerBinaryMathFunction(db, 'power', Math.pow);
    registerBinaryMathFunction(db, 'log', (base, value) => {
        if (base <= 0 || base === 1 || value <= 0) {
            return Number.NaN;
        }

        return Math.log(value) / Math.log(base);
    });

    logger.debug(MODULE_NAME, 'Registered SQLite math function fallbacks');
}

function isHexDigit(value: string): boolean {
    return /^[0-9a-fA-F]$/.test(value);
}

function unhex(value: SqlValue, ignoreChars?: SqlValue): Uint8Array | null {
    if (value === null || value === undefined || ignoreChars === null) {
        return null;
    }

    const text = String(value);
    const ignored = new Set<string>();
    if (ignoreChars !== undefined) {
        for (const char of String(ignoreChars)) {
            if (!isHexDigit(char)) {
                ignored.add(char);
            }
        }
    }

    let pendingNibble: number | null = null;
    const bytes: number[] = [];
    for (const char of text) {
        if (isHexDigit(char)) {
            const nibble = Number.parseInt(char, 16);
            if (pendingNibble === null) {
                pendingNibble = nibble;
            } else {
                bytes.push(pendingNibble * 16 + nibble);
                pendingNibble = null;
            }
        } else if (!ignored.has(char)) {
            return null;
        } else if (pendingNibble !== null) {
            return null;
        }
    }

    if (pendingNibble !== null) {
        return null;
    }

    return new Uint8Array(bytes);
}

function registerBinaryFunctions(db: Database): void {
    db.createFunction({
        name: 'unhex',
        xFunc: (ctxPtr: number, value: SqlValue): Uint8Array | null => unhex(value),
        arity: 1,
        deterministic: true
    });

    db.createFunction({
        name: 'unhex',
        xFunc: (ctxPtr: number, value: SqlValue, ignoreChars: SqlValue): Uint8Array | null =>
            unhex(value, ignoreChars),
        arity: 2,
        deterministic: true
    });

    logger.debug(MODULE_NAME, 'Registered SQLite binary function fallbacks');
}

function registerCompatibilityFunctions(db: Database): void {
    registerBinaryFunctions(db);
    registerSoundexFunction(db);
    registerSha3Function(db);

    logger.debug(MODULE_NAME, 'Registered SQLite compatibility function fallbacks');
}

function registerSoundexFunction(db: Database): void {
    db.createFunction({
        name: 'soundex',
        xFunc: (ctxPtr: number, value: SqlValue): string | null => {
            if (value === null || value === undefined) {
                return null;
            }

            const input = String(value);
            const start = findFirstAsciiLetter(input);
            if (start < 0) {
                return '?000';
            }

            const first = input[start].toUpperCase();
            let previousCode = soundexCode(first);
            let result = first;

            for (let i = start; i < input.length && result.length < 4; i++) {
                const char = input[i];
                const code = soundexCode(char);
                if (code > 0) {
                    if (code !== previousCode) {
                        result += String(code);
                    }
                    previousCode = code;
                } else {
                    previousCode = 0;
                }
            }

            return result.padEnd(4, '0');
        },
        arity: 1,
        deterministic: true
    });
}

function findFirstAsciiLetter(value: string): number {
    for (let i = 0; i < value.length; i++) {
        const code = value.charCodeAt(i);
        if ((code >= 65 && code <= 90) || (code >= 97 && code <= 122)) {
            return i;
        }
    }

    return -1;
}

function soundexCode(value: string): number {
    switch (value.toUpperCase()) {
        case 'B':
        case 'F':
        case 'P':
        case 'V':
            return 1;
        case 'C':
        case 'G':
        case 'J':
        case 'K':
        case 'Q':
        case 'S':
        case 'X':
        case 'Z':
            return 2;
        case 'D':
        case 'T':
            return 3;
        case 'L':
            return 4;
        case 'M':
        case 'N':
            return 5;
        case 'R':
            return 6;
        default:
            return 0;
    }
}

function registerSha3Function(db: Database): void {
    db.createFunction({
        name: 'sha3',
        xFunc: (ctxPtr: number, value: SqlValue): Uint8Array | null => sha3Sql(value, 256),
        arity: 1,
        deterministic: true
    });

    db.createFunction({
        name: 'sha3',
        xFunc: (ctxPtr: number, value: SqlValue, size: SqlValue): Uint8Array | null => {
            const bits = Number(size);
            if (!isSupportedSha3Size(bits)) {
                throw new Error('SHA3 size should be one of: 224 256 384 512');
            }

            return sha3Sql(value, bits);
        },
        arity: 2,
        deterministic: true
    });
}

function sha3Sql(value: SqlValue, bits: number): Uint8Array | null {
    if (value === null || value === undefined) {
        return null;
    }

    const bytes = value instanceof Uint8Array
        ? value
        : new TextEncoder().encode(String(value));

    return sha3(bytes, bits);
}

function isSupportedSha3Size(bits: number): boolean {
    return bits === 224 || bits === 256 || bits === 384 || bits === 512;
}

function sha3(input: Uint8Array, bits: number): Uint8Array {
    const rateBytes = 200 - (bits / 4);
    const outputBytes = bits / 8;
    const state = new Array<bigint>(25).fill(0n);
    const paddedLength = Math.ceil((input.length + 2) / rateBytes) * rateBytes;
    const padded = new Uint8Array(paddedLength);
    padded.set(input);
    padded[input.length] = 0x06;
    padded[padded.length - 1] |= 0x80;

    for (let offset = 0; offset < padded.length; offset += rateBytes) {
        for (let i = 0; i < rateBytes; i++) {
            state[i >> 3] ^= BigInt(padded[offset + i]) << BigInt((i & 7) * 8);
        }

        keccakF1600(state);
    }

    const result = new Uint8Array(outputBytes);
    for (let i = 0; i < outputBytes; i++) {
        result[i] = Number((state[i >> 3] >> BigInt((i & 7) * 8)) & 0xffn);
    }

    return result;
}

const KECCAK_ROTATION_OFFSETS = [
    0, 1, 62, 28, 27,
    36, 44, 6, 55, 20,
    3, 10, 43, 25, 39,
    41, 45, 15, 21, 8,
    18, 2, 61, 56, 14
];

const KECCAK_ROUND_CONSTANTS = [
    0x0000000000000001n, 0x0000000000008082n,
    0x800000000000808an, 0x8000000080008000n,
    0x000000000000808bn, 0x0000000080000001n,
    0x8000000080008081n, 0x8000000000008009n,
    0x000000000000008an, 0x0000000000000088n,
    0x0000000080008009n, 0x000000008000000an,
    0x000000008000808bn, 0x800000000000008bn,
    0x8000000000008089n, 0x8000000000008003n,
    0x8000000000008002n, 0x8000000000000080n,
    0x000000000000800an, 0x800000008000000an,
    0x8000000080008081n, 0x8000000000008080n,
    0x0000000080000001n, 0x8000000080008008n
];

function rotateLeft64(value: bigint, bits: number): bigint {
    if (bits === 0) {
        return value & UINT64_MASK;
    }

    return ((value << BigInt(bits)) | (value >> BigInt(64 - bits))) & UINT64_MASK;
}

function keccakF1600(state: bigint[]): void {
    const c = new Array<bigint>(5);
    const d = new Array<bigint>(5);
    const b = new Array<bigint>(25);

    for (const roundConstant of KECCAK_ROUND_CONSTANTS) {
        for (let x = 0; x < 5; x++) {
            c[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];
        }

        for (let x = 0; x < 5; x++) {
            d[x] = c[(x + 4) % 5] ^ rotateLeft64(c[(x + 1) % 5], 1);
        }

        for (let x = 0; x < 5; x++) {
            for (let y = 0; y < 5; y++) {
                state[x + 5 * y] = (state[x + 5 * y] ^ d[x]) & UINT64_MASK;
            }
        }

        for (let x = 0; x < 5; x++) {
            for (let y = 0; y < 5; y++) {
                b[y + 5 * ((2 * x + 3 * y) % 5)] =
                    rotateLeft64(state[x + 5 * y], KECCAK_ROTATION_OFFSETS[x + 5 * y]);
            }
        }

        for (let x = 0; x < 5; x++) {
            for (let y = 0; y < 5; y++) {
                state[x + 5 * y] = (b[x + 5 * y] ^ ((~b[((x + 1) % 5) + 5 * y]) & b[((x + 2) % 5) + 5 * y])) & UINT64_MASK;
            }
        }

        state[0] = (state[0] ^ roundConstant) & UINT64_MASK;
    }
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

                    const left = parseDecimal(str1);
                    const right = parseDecimal(str2);
                    if (!left || !right) {
                        return 0;
                    }

                    return compareDecimals(left, right);
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
