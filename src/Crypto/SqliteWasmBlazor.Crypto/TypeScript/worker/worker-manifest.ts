// worker-manifest.ts
// Disk-bound passkey manifest operations: read, write, clear. Operates on
// bytes 524..1023 of every DB header sector in the SAHPool. The disk-as-unit
// invariant means every DB must carry byte-identical manifest bytes; a
// mismatch surfaces as state='mismatch' rather than reading any one DB's
// manifest as canonical.

import { clearBytes } from '@sqlitewasmblazor/crypto-core';
import { poolUtil } from '@sqlitewasmblazor/worker-common';
import { hasGlobalKey, snapshotGlobalKey } from './vfs-prf/key-registry';
import {
    deriveManifestMacKey,
    emptyManifestRegion,
    parseManifestRegion,
    serializeManifestRegion,
    type ManifestParseState,
} from './vfs-prf/manifest';

export async function readDiskManifestOp(verifyMac: boolean) {
    if (!poolUtil) {
        throw new Error('SQLite not initialized');
    }

    const databases = poolUtil.listDatabases();
    if (databases.length === 0) {
        // Empty pool — no DBs means no manifest can exist. State is 'absent'.
        return { manifestState: 'absent' };
    }

    let macKey: Uint8Array | undefined;
    if (verifyMac) {
        if (!hasGlobalKey()) {
            throw new Error('readDiskManifest verifyMac=true requires globalKey to be installed');
        }
        const snapshot = snapshotGlobalKey()!;
        try {
            macKey = deriveManifestMacKey(snapshot);
        } finally {
            clearBytes(snapshot);
        }
    }

    let referenceRegion: Uint8Array | undefined;
    let referenceState: ManifestParseState | undefined;
    let referenceBody: Uint8Array | undefined;
    let referenceSchemaVersion: number | undefined;

    try {
        for (const name of databases) {
            const path = `/databases/${name}`;
            const region = poolUtil.readManifestSlot(path);
            try {
                if (referenceRegion === undefined) {
                    const parsed = parseManifestRegion(region, macKey);
                    referenceRegion = new Uint8Array(region);
                    referenceState = parsed.state;
                    referenceBody = parsed.body;
                    referenceSchemaVersion = parsed.schemaVersion;
                } else {
                    // Disk-as-unit: every DB must carry byte-identical bytes.
                    if (region.length !== referenceRegion.length
                        || !regionsEqual(region, referenceRegion)) {
                        return { manifestState: 'mismatch' };
                    }
                }
            } finally {
                clearBytes(region);
            }
        }

        if (referenceState === 'present' && referenceBody !== undefined) {
            const bodyBase64 = bytesToBase64(referenceBody);
            return {
                manifestState: 'present',
                manifestBody: bodyBase64,
                manifestSchemaVersion: referenceSchemaVersion,
            };
        }
        return { manifestState: referenceState ?? 'absent' };
    } finally {
        if (macKey !== undefined) {
            clearBytes(macKey);
        }
        if (referenceRegion !== undefined) {
            clearBytes(referenceRegion);
        }
        if (referenceBody !== undefined) {
            clearBytes(referenceBody);
        }
    }
}

export async function writeDiskManifestOp(body: Uint8Array) {
    if (!poolUtil) {
        throw new Error('SQLite not initialized');
    }
    if (!hasGlobalKey()) {
        throw new Error('writeDiskManifest requires globalKey to be installed (manifest MAC is keyed)');
    }

    const databases = poolUtil.listDatabases();
    if (databases.length === 0) {
        // No DBs → nowhere to write the manifest. Caller is responsible
        // for ensuring at least one DB is encrypted-in-place before
        // calling this op (matches EnterEncryptedAsync's ordering).
        return { rowsAffected: 0 };
    }

    const snapshot = snapshotGlobalKey()!;
    let macKey: Uint8Array | undefined;
    let region: Uint8Array | undefined;
    try {
        macKey = deriveManifestMacKey(snapshot);
        region = serializeManifestRegion(body, macKey);
        for (const name of databases) {
            poolUtil.writeManifestSlot(`/databases/${name}`, region);
        }
        return { rowsAffected: 0 };
    } finally {
        clearBytes(snapshot);
        if (macKey !== undefined) {
            clearBytes(macKey);
        }
        if (region !== undefined) {
            clearBytes(region);
        }
    }
}

export async function clearDiskManifestOp() {
    if (!poolUtil) {
        throw new Error('SQLite not initialized');
    }

    const databases = poolUtil.listDatabases();
    if (databases.length === 0) {
        return { rowsAffected: 0 };
    }

    const empty = emptyManifestRegion();
    for (const name of databases) {
        poolUtil.writeManifestSlot(`/databases/${name}`, empty);
    }
    return { rowsAffected: 0 };
}

function regionsEqual(a: Uint8Array, b: Uint8Array): boolean {
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) {
        if (a[i] !== b[i]) return false;
    }
    return true;
}

function bytesToBase64(bytes: Uint8Array): string {
    let s = '';
    for (let i = 0; i < bytes.length; i++) {
        s += String.fromCharCode(bytes[i]);
    }
    return btoa(s);
}
