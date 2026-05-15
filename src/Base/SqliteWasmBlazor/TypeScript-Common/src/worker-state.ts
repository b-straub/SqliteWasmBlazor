// worker-state.ts
// Shared mutable state and constants for the SQLite worker modules.

import type { SqlValue } from '@sqlite.org/sqlite-wasm';
import { Unpackr } from 'msgpackr';

export let sqlite3: any;
export let poolUtil: any;
export const openDatabases = new Map<string, any>();
export const pragmasSet = new Set<string>();
export const schemaCache = new Map<string, Map<string, string>>();
export let baseHref = '/';

export const MODULE_NAME = 'SQLite Worker';

// Unpackr preserving int64 as BigInt — JS Number loses precision for values > 2^53-1
export const bigIntUnpackr = new Unpackr({ int64AsType: 'bigint' });

export function setSqlite3(instance: any) { sqlite3 = instance; }
export function setPoolUtil(instance: any) { poolUtil = instance; }
export function setBaseHref(href: string) { baseHref = href; }

export type { SqlValue };
