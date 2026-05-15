// Disk-bound passkey manifest for the PRF-keyed VFS.
//
// Layout — bytes 524..1023 of every SAHPool slot's header sector
// (the per-slot first 4096 bytes are SAHPool's plaintext metadata; bytes
// 0..523 are claimed by SAHPool for path/flags/digest, leaving 524..4095
// free; we reserve 524..1023 for this manifest, leaving 1024..4095 for
// future use).
//
//   524     4   magic = "PFAM"   (PRF-VFS Passkey Manifest)
//   528     1   schemaVersion    (0x01 — gradual-evolution knob)
//   529     1   reserved         (0x00)
//   530     2   bodyLength N     (uint16 LE; bytes that follow before pad)
//   532     N   body             MessagePack { credentialId, pubkeyFingerprint }
//   532+N   …   zero-pad to 991
//   992    32   HMAC-SHA256(macKey, bytes[524..991])
//  1024     …   (rest of header sector unused)
//
// macKey is HKDF(globalVfsKey, salt=∅, info="sqlite-vfs:manifest-mac:v1", L=32)
// — domain-separated from anything else derived from the global key, so a
// future key-cache rotation that bumps the info string for one consumer
// does not silently invalidate the manifest's MAC.
//
// The manifest body is plaintext so a pre-unlock reader can surface the
// credentialId for the auth-flow fast-fail check. Tamper detection lands
// post-unlock via the HMAC, which reuses the global VFS key (any rekey
// rotates the macKey too).
//
// Disk-as-unit invariant: every DB in the pool carries the *same* manifest.
// Mismatch across DBs is reported as a typed `mismatch` state — corruption
// or partial-import accident.

import {
    clearBytes,
    deriveHkdfKey,
    hmac,
    sha256,
} from '@sqlitewasmblazor/crypto-core';

// Layout constants (absolute byte offsets within the SAHPool header sector
// AND offsets within the manifest region itself — both are kept here so a
// reader does not have to do offset arithmetic).
export const MANIFEST_OFFSET = 524;
export const MANIFEST_LENGTH = 500;
export const MANIFEST_END = MANIFEST_OFFSET + MANIFEST_LENGTH; // 1024
const MAC_LENGTH = 32;
const MAC_OFFSET_REL = MANIFEST_LENGTH - MAC_LENGTH; // 468 — relative to manifest start
const HEADER_LEN = 8; // magic(4) + version(1) + reserved(1) + bodyLen(2)
const BODY_OFFSET_REL = HEADER_LEN; // 8
const MAX_BODY_LEN = MAC_OFFSET_REL - HEADER_LEN; // 460

const MAGIC = new Uint8Array([0x50, 0x46, 0x41, 0x4d]); // "PFAM"
const SCHEMA_VERSION_V1 = 0x01;
const HKDF_INFO = 'sqlite-vfs:manifest-mac:v1';

/**
 * Derive the manifest MAC key from the worker's global VFS key.
 * Caller owns the returned buffer and must `clearBytes` it.
 *
 * Uses {@link deriveHkdfKey} (HKDF-SHA256, empty salt) — same primitive
 * the C# side uses for its own domain-separated VFS keys, just with a
 * dedicated info string so the manifest MAC key is independent of
 * anything else derived from the global key.
 */
export function deriveManifestMacKey(globalKey: Uint8Array): Uint8Array {
    return deriveHkdfKey(globalKey, HKDF_INFO, 32);
}

/**
 * `'absent'`   — magic missing (v1 disk or freshly cleared region).
 * `'present'`  — magic + valid layout. `body` populated.
 * `'tampered'` — magic + valid layout but HMAC verification failed
 *                (only set when caller passed `macKey`).
 * `'malformed'`— magic present but layout decode failed (e.g. bodyLen out
 *                of range). Treated as a hard error — disk is corrupted.
 */
export type ManifestParseState = 'absent' | 'present' | 'tampered' | 'malformed';

export interface ManifestParseResult {
    state: ManifestParseState;
    body?: Uint8Array;
    schemaVersion?: number;
}

/**
 * Decode bytes at offset 524..1023. Pass `macKey` to also verify the MAC
 * (post-unlock). When `macKey` is undefined, MAC is not checked and the
 * caller gets `'present'` for any structurally-valid manifest.
 *
 * The returned `body` is a copy — caller owns it (the input region typically
 * came from a transient SAH read buffer).
 */
export function parseManifestRegion(
    region: Uint8Array,
    macKey?: Uint8Array,
): ManifestParseResult {
    if (region.length !== MANIFEST_LENGTH) {
        return { state: 'malformed' };
    }

    // Magic absent → fresh / cleared.
    let magicMatch = true;
    for (let i = 0; i < MAGIC.length; i++) {
        if (region[i] !== MAGIC[i]) {
            magicMatch = false;
            break;
        }
    }
    if (!magicMatch) {
        // Treat all-zero magic as 'absent'; any other non-magic value as
        // 'malformed' so corruption can't masquerade as a fresh disk.
        const allZeroMagic = region[0] === 0 && region[1] === 0
            && region[2] === 0 && region[3] === 0;
        return { state: allZeroMagic ? 'absent' : 'malformed' };
    }

    const schemaVersion = region[4];
    const bodyLen = region[530 - MANIFEST_OFFSET]
        | (region[531 - MANIFEST_OFFSET] << 8);
    if (bodyLen < 0 || bodyLen > MAX_BODY_LEN) {
        return { state: 'malformed' };
    }

    if (macKey !== undefined) {
        const macInput = region.subarray(0, MAC_OFFSET_REL);
        const expectedMac = region.subarray(MAC_OFFSET_REL, MANIFEST_LENGTH);
        const computedMac = hmac(sha256, macKey, macInput);
        try {
            if (!constantTimeEqual(computedMac, expectedMac)) {
                return { state: 'tampered', schemaVersion };
            }
        } finally {
            clearBytes(computedMac);
        }
    }

    // body copy so caller can hand it off without holding the SAH read buffer.
    const body = new Uint8Array(bodyLen);
    body.set(region.subarray(BODY_OFFSET_REL, BODY_OFFSET_REL + bodyLen));
    return { state: 'present', body, schemaVersion };
}

/**
 * Build the 500-byte manifest region for a body + MAC key. Throws when
 * `body` exceeds the available space.
 *
 * Caller owns the returned buffer — typically passed straight into
 * `sah.write({ at: MANIFEST_OFFSET })`.
 */
export function serializeManifestRegion(
    body: Uint8Array,
    macKey: Uint8Array,
): Uint8Array {
    if (body.length > MAX_BODY_LEN) {
        throw new Error(
            `Disk manifest body too large: ${body.length} bytes ` +
            `(max ${MAX_BODY_LEN}). Increase MANIFEST_LENGTH or compress the body.`);
    }

    const region = new Uint8Array(MANIFEST_LENGTH);
    region.set(MAGIC, 0);
    region[4] = SCHEMA_VERSION_V1;
    region[5] = 0; // reserved
    region[6] = body.length & 0xff;
    region[7] = (body.length >>> 8) & 0xff;
    region.set(body, BODY_OFFSET_REL);
    // bytes [BODY_OFFSET_REL + body.length .. MAC_OFFSET_REL] stay zero.

    const mac = hmac(sha256, macKey, region.subarray(0, MAC_OFFSET_REL));
    try {
        region.set(mac, MAC_OFFSET_REL);
    } finally {
        clearBytes(mac);
    }
    return region;
}

/**
 * 500 bytes of zeros — used by `clearDiskManifest`. Returns a fresh buffer
 * each call (callers may keep the reference past write).
 */
export function emptyManifestRegion(): Uint8Array {
    return new Uint8Array(MANIFEST_LENGTH);
}

function constantTimeEqual(a: Uint8Array, b: Uint8Array): boolean {
    if (a.length !== b.length) {
        return false;
    }
    let diff = 0;
    for (let i = 0; i < a.length; i++) {
        diff |= a[i] ^ b[i];
    }
    return diff === 0;
}
