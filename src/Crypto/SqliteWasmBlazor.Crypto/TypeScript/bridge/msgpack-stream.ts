// msgpack-stream.ts
// Minimal positional-array msgpack encoder for streaming the
// EncryptedDiskEnvelope wire shape into a composed Blob.
//
// Why custom: msgpackr's `pack(envelope)` materialises the entire envelope
// as a single Uint8Array (~250 MB for a single-DB pool). Mobile browser
// renderer caps (Mobile Safari ~380 MB; Android Chrome varies by device,
// often lower than desktop) can't fit that buffer alongside the rekey
// output. Encoding just the headers + leaving per-DB ciphertext as Blob
// parts lets `new Blob(parts)` reference disk-backed segments without
// ever holding the full envelope in JS heap.
//
// Scope: ONLY what EncryptedDiskEnvelope (MessagePack-CSharp positional
// `[Key(N)]` shape) needs. No maps, no ext types, no floats.

const textEncoder = new TextEncoder();

/**
 * msgpack array header for a known element count. Caller emits the N
 * elements afterwards in order. Supports up to 2^32-1 elements.
 */
export function packArrayHeader(n: number): Uint8Array {
    if (n < 0) {
        throw new RangeError(`packArrayHeader: negative count ${n}`);
    }
    if (n < 16) {
        return new Uint8Array([0x90 | n]);
    }
    if (n < 0x10000) {
        const buf = new Uint8Array(3);
        buf[0] = 0xdc;
        buf[1] = (n >> 8) & 0xff;
        buf[2] = n & 0xff;
        return buf;
    }
    if (n <= 0xffffffff) {
        const buf = new Uint8Array(5);
        buf[0] = 0xdd;
        buf[1] = (n >>> 24) & 0xff;
        buf[2] = (n >>> 16) & 0xff;
        buf[3] = (n >>> 8) & 0xff;
        buf[4] = n & 0xff;
        return buf;
    }
    throw new RangeError(`packArrayHeader: count ${n} exceeds array32 max`);
}

/**
 * msgpack string. Returns header + UTF-8 bytes as two parts so callers can
 * push directly into a Blob parts array without an intermediate copy.
 */
export function packStr(s: string): Uint8Array[] {
    const utf8 = textEncoder.encode(s);
    const len = utf8.length;
    let header: Uint8Array;
    if (len < 32) {
        header = new Uint8Array([0xa0 | len]);
    } else if (len < 256) {
        header = new Uint8Array([0xd9, len]);
    } else if (len < 0x10000) {
        header = new Uint8Array([0xda, (len >> 8) & 0xff, len & 0xff]);
    } else if (len <= 0xffffffff) {
        header = new Uint8Array([
            0xdb,
            (len >>> 24) & 0xff,
            (len >>> 16) & 0xff,
            (len >>> 8) & 0xff,
            len & 0xff,
        ]);
    } else {
        throw new RangeError(`packStr: string of ${len} bytes exceeds str32 max`);
    }
    return [header, utf8];
}

/**
 * msgpack bin header for `len` ciphertext bytes. Caller emits the bytes
 * afterwards as a separate Blob part. Splitting the header off the payload
 * is what lets per-DB rekey buffers stay as standalone Blobs (disk-backed
 * on Safari) instead of being copied into a contiguous output buffer.
 */
export function packBinHeader(len: number): Uint8Array {
    if (len < 0) {
        throw new RangeError(`packBinHeader: negative length ${len}`);
    }
    if (len < 256) {
        return new Uint8Array([0xc4, len]);
    }
    if (len < 0x10000) {
        return new Uint8Array([0xc5, (len >> 8) & 0xff, len & 0xff]);
    }
    if (len <= 0xffffffff) {
        return new Uint8Array([
            0xc6,
            (len >>> 24) & 0xff,
            (len >>> 16) & 0xff,
            (len >>> 8) & 0xff,
            len & 0xff,
        ]);
    }
    throw new RangeError(`packBinHeader: length ${len} exceeds bin32 max`);
}

/**
 * msgpack unsigned integer. Sufficient for envelope `Version` (small
 * positive fixint). Throws on negative or non-finite input — the envelope
 * never carries signed or fractional integers.
 */
export function packUint(n: number): Uint8Array {
    if (!Number.isInteger(n) || n < 0) {
        throw new RangeError(`packUint: ${n} is not a non-negative integer`);
    }
    if (n < 128) {
        return new Uint8Array([n]);
    }
    if (n < 256) {
        return new Uint8Array([0xcc, n]);
    }
    if (n < 0x10000) {
        return new Uint8Array([0xcd, (n >> 8) & 0xff, n & 0xff]);
    }
    if (n <= 0xffffffff) {
        return new Uint8Array([
            0xce,
            (n >>> 24) & 0xff,
            (n >>> 16) & 0xff,
            (n >>> 8) & 0xff,
            n & 0xff,
        ]);
    }
    throw new RangeError(`packUint: ${n} exceeds uint32 max (use uint64 if added)`);
}
