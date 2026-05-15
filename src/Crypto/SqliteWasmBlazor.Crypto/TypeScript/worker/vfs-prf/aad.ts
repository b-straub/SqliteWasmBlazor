// Per-page AAD for ChaCha20-Poly1305 in the PRF-keyed VFS.
//
// Format: "prf-vfs-v1|" + dbPath + "|" + slotIndex (little-endian uint32)
//
// Binds the encryption to:
//   - AAD-version prefix — lets us evolve the envelope without cross-version confusion
//   - dbPath            — prevents ciphertext swap between two DBs under the same key
//   - slotIndex         — prevents page reordering within the same DB
//
// File-type (MAIN_DB vs WAL vs JOURNAL) is intentionally NOT bound. SQLite's
// crash-recovery writes journal/WAL bytes back into the main DB at the same
// offsets; binding the file-type would break replay.

const AAD_PREFIX = 'prf-vfs-v1|';
const textEncoder = new TextEncoder();

export function buildPageAad(dbPath: string, slotIndex: number): Uint8Array {
    const prefixBytes = textEncoder.encode(AAD_PREFIX + dbPath + '|');
    const aad = new Uint8Array(prefixBytes.length + 4);
    aad.set(prefixBytes, 0);

    const dv = new DataView(aad.buffer, aad.byteOffset + prefixBytes.length, 4);
    dv.setUint32(0, slotIndex >>> 0, true); // little-endian

    return aad;
}
