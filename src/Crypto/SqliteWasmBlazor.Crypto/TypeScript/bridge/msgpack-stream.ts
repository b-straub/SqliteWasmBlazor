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
 * Streaming reader wrapping a `ReadableStream<Uint8Array>` (typically
 * `blob.stream().getReader()`). Pulls underlying chunks on demand and
 * exposes byte-level read/skip primitives so a forward-only msgpack
 * decoder can consume arbitrarily large streams without ever buffering
 * the whole envelope in JS heap.
 *
 * The internal `_queue` holds zero or more pending chunks (subarray views
 * into the underlying reader chunks); `read(n)` and `skip(n)` consume
 * across chunk boundaries, releasing exhausted chunks as it goes.
 */
export class BufferedStreamReader {
    private _reader: ReadableStreamDefaultReader<Uint8Array>;
    private _queue: Uint8Array[] = [];
    private _queuedBytes = 0;
    private _exhausted = false;

    constructor(reader: ReadableStreamDefaultReader<Uint8Array>) {
        this._reader = reader;
    }

    /**
     * Read exactly `n` bytes. Returns a fresh `Uint8Array` of length `n`
     * (always a copy — the source chunks may be reused by the underlying
     * stream after the next pull). Throws if the stream ends before `n`
     * bytes are available.
     */
    async read(n: number): Promise<Uint8Array> {
        if (n < 0) {
            throw new RangeError(`BufferedStreamReader.read: negative length ${n}`);
        }
        if (n === 0) {
            return new Uint8Array(0);
        }
        await this._ensure(n);
        const out = new Uint8Array(n);
        let written = 0;
        while (written < n) {
            const head = this._queue[0];
            const take = Math.min(head.length, n - written);
            out.set(head.subarray(0, take), written);
            written += take;
            if (take === head.length) {
                this._queue.shift();
            } else {
                this._queue[0] = head.subarray(take);
            }
            this._queuedBytes -= take;
        }
        return out;
    }

    /**
     * Advance the stream by `n` bytes without returning them. Used to
     * discard payload bytes during preflight (read-and-throw-away).
     */
    async skip(n: number): Promise<void> {
        if (n < 0) {
            throw new RangeError(`BufferedStreamReader.skip: negative length ${n}`);
        }
        let remaining = n;
        while (remaining > 0) {
            if (this._queuedBytes === 0 && !this._exhausted) {
                await this._pull();
                if (this._queuedBytes === 0) {
                    throw new RangeError(
                        `BufferedStreamReader.skip: stream ended with ${remaining} bytes unread`);
                }
            }
            if (this._queuedBytes === 0) {
                throw new RangeError(
                    `BufferedStreamReader.skip: stream ended with ${remaining} bytes unread`);
            }
            const head = this._queue[0];
            const take = Math.min(head.length, remaining);
            if (take === head.length) {
                this._queue.shift();
            } else {
                this._queue[0] = head.subarray(take);
            }
            this._queuedBytes -= take;
            remaining -= take;
        }
    }

    /**
     * Release the underlying reader. Caller should invoke this once the
     * stream is no longer needed (success or failure).
     */
    releaseLock(): void {
        try {
            this._reader.releaseLock();
        } catch {
            // best-effort; the reader may already be released
        }
    }

    private async _ensure(n: number): Promise<void> {
        while (this._queuedBytes < n && !this._exhausted) {
            await this._pull();
        }
        if (this._queuedBytes < n) {
            throw new RangeError(
                `BufferedStreamReader: stream ended after ${this._queuedBytes} bytes, needed ${n}`);
        }
    }

    private async _pull(): Promise<void> {
        const { value, done } = await this._reader.read();
        if (done) {
            this._exhausted = true;
            return;
        }
        if (value && value.length > 0) {
            this._queue.push(value);
            this._queuedBytes += value.length;
        }
    }
}

/**
 * Read a msgpack unsigned int (positive-fixint / uint8 / uint16 / uint32).
 * Returns the numeric value. Rejects negative, ext, float, or oversized
 * representations — sufficient for envelope `Version` field.
 */
export async function readUint(reader: BufferedStreamReader): Promise<number> {
    const [tag] = await reader.read(1);
    if (tag <= 0x7f) {
        return tag;
    }
    if (tag === 0xcc) {
        const [v] = await reader.read(1);
        return v;
    }
    if (tag === 0xcd) {
        const buf = await reader.read(2);
        return (buf[0] << 8) | buf[1];
    }
    if (tag === 0xce) {
        const buf = await reader.read(4);
        return ((buf[0] * 0x1000000) + ((buf[1] << 16) | (buf[2] << 8) | buf[3])) >>> 0;
    }
    throw new Error(`readUint: unexpected tag 0x${tag.toString(16)} (expected fixint / uint8 / uint16 / uint32)`);
}

/**
 * Read a msgpack array header. Returns the element count. Caller is
 * responsible for reading the N elements that follow.
 */
export async function readArrayHeader(reader: BufferedStreamReader): Promise<number> {
    const [tag] = await reader.read(1);
    if ((tag & 0xf0) === 0x90) {
        return tag & 0x0f;
    }
    if (tag === 0xdc) {
        const buf = await reader.read(2);
        return (buf[0] << 8) | buf[1];
    }
    if (tag === 0xdd) {
        const buf = await reader.read(4);
        return ((buf[0] * 0x1000000) + ((buf[1] << 16) | (buf[2] << 8) | buf[3])) >>> 0;
    }
    throw new Error(`readArrayHeader: unexpected tag 0x${tag.toString(16)} (expected fixarray / array16 / array32)`);
}

/**
 * Read a msgpack string (fixstr / str8 / str16 / str32). Returns the
 * UTF-8 decoded string.
 */
export async function readStr(reader: BufferedStreamReader): Promise<string> {
    const [tag] = await reader.read(1);
    let len: number;
    if ((tag & 0xe0) === 0xa0) {
        len = tag & 0x1f;
    } else if (tag === 0xd9) {
        const [v] = await reader.read(1);
        len = v;
    } else if (tag === 0xda) {
        const buf = await reader.read(2);
        len = (buf[0] << 8) | buf[1];
    } else if (tag === 0xdb) {
        const buf = await reader.read(4);
        len = ((buf[0] * 0x1000000) + ((buf[1] << 16) | (buf[2] << 8) | buf[3])) >>> 0;
    } else {
        throw new Error(`readStr: unexpected tag 0x${tag.toString(16)} (expected fixstr / str8 / str16 / str32)`);
    }
    const bytes = await reader.read(len);
    return new TextDecoder().decode(bytes);
}

/**
 * Read a msgpack bin header (bin8 / bin16 / bin32). Returns the payload
 * length. Caller reads the payload via `reader.read(len)` or processes
 * it in chunks (e.g. slot-by-slot during streaming rekey).
 */
export async function readBinHeader(reader: BufferedStreamReader): Promise<number> {
    const [tag] = await reader.read(1);
    if (tag === 0xc4) {
        const [v] = await reader.read(1);
        return v;
    }
    if (tag === 0xc5) {
        const buf = await reader.read(2);
        return (buf[0] << 8) | buf[1];
    }
    if (tag === 0xc6) {
        const buf = await reader.read(4);
        return ((buf[0] * 0x1000000) + ((buf[1] << 16) | (buf[2] << 8) | buf[3])) >>> 0;
    }
    throw new Error(`readBinHeader: unexpected tag 0x${tag.toString(16)} (expected bin8 / bin16 / bin32)`);
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
