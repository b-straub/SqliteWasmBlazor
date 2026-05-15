// type-conversion.ts
// MessagePack ↔ SQLite value conversion functions.
// Used by both bulk-ops (plain import) and crypto-delta (encrypted import/export).

import { logger } from './sqlite-logger';
import { MODULE_NAME, type SqlValue } from './worker-state';

/**
 * Convert a MessagePack-deserialized value to the SQLite bind() format.
 * Uses csharpType from column metadata to determine conversion.
 */
export function convertValueForSqlite(value: any, csharpType: string, sqlType: string): SqlValue {
    if (value === null || value === undefined) {
        return null;
    }

    // Strip nullable suffix for matching
    const baseType = csharpType.endsWith('?') ? csharpType.slice(0, -1) : csharpType;

    switch (baseType) {
        case 'Guid': {
            // MessagePack-CSharp serializes Guid as 36-char string "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
            if (sqlType === 'BLOB') {
                // Convert to 16-byte Uint8Array matching .NET Guid.ToByteArray() layout:
                // Groups 1-3 are little-endian, groups 4-5 are big-endian
                const hex = (value as string).replace(/-/g, '');
                const bytes = new Uint8Array(16);
                // Group 1 (4 bytes, LE): hex[0..7] reversed
                bytes[0] = parseInt(hex.substring(6, 8), 16);
                bytes[1] = parseInt(hex.substring(4, 6), 16);
                bytes[2] = parseInt(hex.substring(2, 4), 16);
                bytes[3] = parseInt(hex.substring(0, 2), 16);
                // Group 2 (2 bytes, LE): hex[8..11] reversed
                bytes[4] = parseInt(hex.substring(10, 12), 16);
                bytes[5] = parseInt(hex.substring(8, 10), 16);
                // Group 3 (2 bytes, LE): hex[12..15] reversed
                bytes[6] = parseInt(hex.substring(14, 16), 16);
                bytes[7] = parseInt(hex.substring(12, 14), 16);
                // Groups 4-5 (8 bytes, BE): hex[16..31] as-is
                for (let i = 8; i < 16; i++) {
                    bytes[i] = parseInt(hex.substring(i * 2, i * 2 + 2), 16);
                }
                return bytes as any;
            }
            // TEXT column: pass string as-is
            return String(value);
        }

        case 'DateTime':
            // MessagePack-CSharp: Timestamp ext (-1) → msgpackr: Date object
            if (value instanceof Date) {
                return value.toISOString();
            }
            return String(value);

        case 'DateTimeOffset':
            // MessagePack-CSharp: array [DateTime, short(offset minutes)]
            // msgpackr: [Date, number]
            if (Array.isArray(value) && value.length === 2 && value[0] instanceof Date) {
                return value[0].toISOString();
            }
            if (value instanceof Date) {
                return value.toISOString();
            }
            return String(value);

        case 'TimeSpan':
            // MessagePack-CSharp serializes as int64 (Ticks)
            if (sqlType === 'TEXT') {
                // Convert Ticks to .NET TimeSpan string format: [d.]hh:mm:ss[.fffffff]
                const ticks = Number(value);
                const negative = ticks < 0;
                const absTicks = Math.abs(ticks);
                const totalSeconds = Math.floor(absTicks / 10000000);
                const fraction = absTicks % 10000000;
                const days = Math.floor(totalSeconds / 86400);
                const hours = Math.floor((totalSeconds % 86400) / 3600);
                const minutes = Math.floor((totalSeconds % 3600) / 60);
                const seconds = totalSeconds % 60;
                const sign = negative ? '-' : '';
                const daysPart = days > 0 ? `${days}.` : '';
                const fractionPart = fraction > 0 ? `.${fraction.toString().padStart(7, '0')}` : '';
                return `${sign}${daysPart}${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}${fractionPart}`;
            }
            // INTEGER column: store as ticks directly
            return Number(value);

        case 'Boolean':
            return value ? 1 : 0;

        case 'String':
            return String(value);

        case 'Decimal':
            // MessagePack-CSharp: string representation → pass through as TEXT
            return String(value);

        case 'Int16':
        case 'Int32':
        case 'Byte':
        case 'UInt32':
            return Number(value);

        case 'Int64':
        case 'UInt64':
            // Bind as text to avoid int64 precision loss at JS↔WASM boundary.
            // SQLite INTEGER affinity coerces text→int64 correctly in C code.
            return String(value);

        case 'Double':
        case 'Single':
            return Number(value);

        case 'Char':
        case 'UInt16':
            // MessagePack-CSharp: char as uint16 → msgpackr: number
            // SQLite stores as TEXT (single character)
            if (typeof value === 'number') {
                return String.fromCharCode(value);
            }
            return String(value);

        case 'Enum':
            // MessagePack-CSharp: enum as underlying int → msgpackr: number
            return Number(value);

        case 'JsonArray':
            // EF Core JSON value converter: Array → JSON.stringify for TEXT column
            if (Array.isArray(value)) {
                return JSON.stringify(value);
            }
            return String(value);

        case 'ByteArray':
            // Already Uint8Array from msgpackr
            return value as any;

        default:
            logger.warn(MODULE_NAME, `convertValueForSqlite: unhandled type "${csharpType}", passing through`);
            return value as SqlValue;
    }
}

/**
 * Convert a SQLite value back to MessagePack-CSharp wire format for export.
 * This ensures exported files are compatible with C#'s MessagePackSerializer.Deserialize.
 */
export function convertValueFromSqlite(value: any, csharpType: string, sqlType: string): any {
    if (value === null || value === undefined) {
        return null;
    }

    const baseType = csharpType.endsWith('?') ? csharpType.slice(0, -1) : csharpType;

    switch (baseType) {
        case 'Guid': {
            // SQLite stores as BLOB (Uint8Array) or TEXT (string)
            // MessagePack-CSharp expects: 36-char string "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
            if (value instanceof Uint8Array && value.length === 16) {
                // .NET Guid.ToByteArray() layout: groups 1-3 little-endian, 4-5 big-endian
                const h = (i: number) => value[i].toString(16).padStart(2, '0');
                // Group 1 (4 bytes LE → reverse for hex string)
                const g1 = h(3) + h(2) + h(1) + h(0);
                // Group 2 (2 bytes LE → reverse)
                const g2 = h(5) + h(4);
                // Group 3 (2 bytes LE → reverse)
                const g3 = h(7) + h(6);
                // Groups 4-5 (8 bytes BE → as-is)
                const g4 = h(8) + h(9);
                const g5 = h(10) + h(11) + h(12) + h(13) + h(14) + h(15);
                return `${g1}-${g2}-${g3}-${g4}-${g5}`;
            }
            // Already a string (TEXT storage)
            return String(value);
        }

        case 'DateTime': {
            // SQLite stores as TEXT (ISO 8601)
            // MessagePack-CSharp expects: Timestamp ext (-1) → pack as Date object
            // msgpackr packs Date as Timestamp ext automatically
            if (typeof value === 'string') {
                return new Date(value);
            }
            return value;
        }

        case 'DateTimeOffset': {
            // SQLite stores as TEXT (ISO 8601 with offset)
            // MessagePack-CSharp expects: array [DateTime, short(offset minutes)]
            if (typeof value === 'string') {
                const d = new Date(value);
                // Extract offset from ISO string (e.g., "+02:00" or "Z")
                const match = value.match(/([+-])(\d{2}):(\d{2})$/);
                let offsetMinutes = 0;
                if (match) {
                    offsetMinutes = (parseInt(match[2]) * 60 + parseInt(match[3])) * (match[1] === '-' ? -1 : 1);
                }
                return [d, offsetMinutes];
            }
            return value;
        }

        case 'TimeSpan': {
            // SQLite stores as TEXT (e.g., "1.02:03:04.0050000")
            // MessagePack-CSharp expects: int64 (Ticks)
            if (typeof value === 'string') {
                // Parse .NET TimeSpan string format: [d.]hh:mm:ss[.fffffff]
                const parts = value.match(/^(-?)(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?$/);
                if (parts) {
                    const sign = parts[1] === '-' ? -1 : 1;
                    const days = parseInt(parts[2] || '0');
                    const hours = parseInt(parts[3]);
                    const minutes = parseInt(parts[4]);
                    const seconds = parseInt(parts[5]);
                    const fraction = parts[6] || '0';
                    // Ticks = 10,000,000 per second
                    const ticks = sign * (
                        ((days * 24 + hours) * 3600 + minutes * 60 + seconds) * 10000000 +
                        parseInt(fraction.padEnd(7, '0').slice(0, 7))
                    );
                    return ticks;
                }
            }
            // Numeric (stored as days or ticks)
            return Number(value);
        }

        case 'Boolean':
            // SQLite stores as INTEGER (0/1)
            // MessagePack-CSharp expects: true/false
            return value === 1 || value === true;

        case 'Decimal':
            // SQLite stores as TEXT, MessagePack-CSharp expects: string
            return String(value);

        case 'Char':
            // SQLite stores as TEXT, MessagePack-CSharp expects: uint16 (char code)
            if (typeof value === 'string' && value.length >= 1) {
                return value.charCodeAt(0);
            }
            return 0;

        case 'Enum':
            // SQLite stores as INTEGER, MessagePack-CSharp expects: integer
            return Number(value);

        case 'Int16':
        case 'Int32':
        case 'Byte':
        case 'UInt16':
        case 'UInt32':
            return Number(value);

        case 'Int64':
        case 'UInt64':
            // Read as SQLITE_TEXT in bulkExport to avoid sqlite3_column_int64 boundary errors.
            // Value arrives here as BigInt (from text parse) — pass through for msgpackr int64 packing.
            if (typeof value === 'bigint') {
                return value;
            }
            return Number(value);

        case 'Double':
        case 'Single':
            return Number(value);

        case 'String':
            return String(value);

        case 'JsonArray':
            // SQLite TEXT (JSON string) → parse to array for MessagePack serialization
            if (typeof value === 'string') {
                try {
                    return JSON.parse(value);
                } catch {
                    return value;
                }
            }
            return value;

        case 'ByteArray':
            // SQLite BLOB → already Uint8Array → msgpackr packs as bin (compatible)
            return value;

        default:
            logger.warn(MODULE_NAME, `convertValueFromSqlite: unhandled type "${csharpType}", passing through`);
            return value;
    }
}
