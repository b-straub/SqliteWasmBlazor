// opfs-sahpool.ts
var SECTOR_SIZE = 4096;
var HEADER_MAX_PATH_SIZE = 512;
var HEADER_FLAGS_SIZE = 4;
var HEADER_DIGEST_SIZE = 8;
var HEADER_CORPUS_SIZE = HEADER_MAX_PATH_SIZE + HEADER_FLAGS_SIZE;
var HEADER_OFFSET_FLAGS = HEADER_MAX_PATH_SIZE;
var HEADER_OFFSET_DIGEST = HEADER_CORPUS_SIZE;
var HEADER_OFFSET_DATA = SECTOR_SIZE;
var SQLITE_OPEN_MAIN_DB = 256;
var SQLITE_OPEN_MAIN_JOURNAL = 2048;
var SQLITE_OPEN_SUPER_JOURNAL = 16384;
var SQLITE_OPEN_WAL = 524288;
var SQLITE_OPEN_CREATE = 4;
var SQLITE_OPEN_DELETEONCLOSE = 8;
var SQLITE_OPEN_MEMORY = 128;
var PERSISTENT_FILE_TYPES = SQLITE_OPEN_MAIN_DB | SQLITE_OPEN_MAIN_JOURNAL | SQLITE_OPEN_SUPER_JOURNAL | SQLITE_OPEN_WAL;
var FLAG_COMPUTE_DIGEST_V2 = SQLITE_OPEN_MEMORY;
var OPAQUE_DIR_NAME = ".opaque";
var SQLITE_OK = 0;
var SQLITE_ERROR = 1;
var SQLITE_IOERR = 10;
var SQLITE_IOERR_SHORT_READ = 522;
var SQLITE_IOERR_WRITE = 778;
var SQLITE_IOERR_READ = 266;
var SQLITE_LOCK_NONE = 0;
var getRandomName = () => Math.random().toString(36).slice(2);
var textDecoder = new TextDecoder();
var textEncoder = new TextEncoder();
var OpfsSAHPool = class {
  constructor(options = {}) {
    this.#dhVfsRoot = null;
    this.#dhOpaque = null;
    this.#dhVfsParent = null;
    // Pool management
    this.#mapSAHToName = /* @__PURE__ */ new Map();
    // SAH -> random OPFS filename
    this.#mapFilenameToSAH = /* @__PURE__ */ new Map();
    // SQLite path -> SAH
    this.#availableSAH = /* @__PURE__ */ new Set();
    // Unassociated SAHs ready for use
    // File handle tracking for open files
    this.#mapFileIdToFile = /* @__PURE__ */ new Map();
    // fileId -> {path, sah, lockType, flags}
    this.#nextFileId = 1;
    // Header buffer for reading/writing file metadata
    this.#apBody = new Uint8Array(HEADER_CORPUS_SIZE);
    this.#dvBody = null;
    this.vfsDir = null;
    this.vfsDir = options.directory || ".opfs-sahpool";
    this.#dvBody = new DataView(this.#apBody.buffer, this.#apBody.byteOffset);
    this.isReady = this.reset(options.clearOnInit || false).then(() => {
      const capacity = this.getCapacity();
      if (capacity > 0) {
        return Promise.resolve();
      }
      return this.addCapacity(options.initialCapacity || 6);
    });
  }
  #dhVfsRoot;
  #dhOpaque;
  #dhVfsParent;
  #mapSAHToName;
  #mapFilenameToSAH;
  #availableSAH;
  #mapFileIdToFile;
  #nextFileId;
  #apBody;
  #dvBody;
  log(...args) {
    console.log("[OpfsSAHPool]", ...args);
  }
  warn(...args) {
    console.warn("[OpfsSAHPool]", ...args);
  }
  error(...args) {
    console.error("[OpfsSAHPool]", ...args);
  }
  getCapacity() {
    return this.#mapSAHToName.size;
  }
  getFileCount() {
    return this.#mapFilenameToSAH.size;
  }
  getFileNames() {
    return Array.from(this.#mapFilenameToSAH.keys());
  }
  /**
   * Add capacity - create n new OPFS files with sync access handles
   */
  async addCapacity(n) {
    for (let i = 0; i < n; ++i) {
      const name = getRandomName();
      const h = await this.#dhOpaque.getFileHandle(name, { create: true });
      const ah = await h.createSyncAccessHandle();
      this.#mapSAHToName.set(ah, name);
      this.setAssociatedPath(ah, "", 0);
    }
    this.log(`Added ${n} handles, total capacity: ${this.getCapacity()}`);
    return this.getCapacity();
  }
  /**
   * Release all access handles (cleanup)
   */
  releaseAccessHandles() {
    for (const ah of this.#mapSAHToName.keys()) {
      try {
        ah.close();
      } catch (e) {
        this.warn("Error closing handle:", e);
      }
    }
    this.#mapSAHToName.clear();
    this.#mapFilenameToSAH.clear();
    this.#availableSAH.clear();
    this.#mapFileIdToFile.clear();
  }
  /**
   * Acquire all existing access handles from OPFS directory with retry logic
   */
  async acquireAccessHandles(clearFiles = false) {
    const files = [];
    for await (const [name, h] of this.#dhOpaque) {
      if ("file" === h.kind) {
        files.push([name, h]);
      }
    }
    const maxRetries = 3;
    const retryDelay = 100;
    for (let attempt = 0; attempt < maxRetries; attempt++) {
      if (attempt > 0) {
        this.warn(`Retry ${attempt}/${maxRetries - 1} after ${retryDelay}ms delay...`);
        await new Promise((resolve) => setTimeout(resolve, retryDelay * attempt));
      }
      const results = await Promise.allSettled(
        files.map(async ([name, h]) => {
          try {
            const ah = await h.createSyncAccessHandle();
            this.#mapSAHToName.set(ah, name);
            if (clearFiles) {
              ah.truncate(HEADER_OFFSET_DATA);
              this.setAssociatedPath(ah, "", 0);
            } else {
              const path = this.getAssociatedPath(ah);
              if (path) {
                this.#mapFilenameToSAH.set(path, ah);
                this.log(`Restored file association: ${path} -> ${name}`);
              } else {
                this.#availableSAH.add(ah);
              }
            }
          } catch (e) {
            if (e.name === "NoModificationAllowedError") {
              throw e;
            } else {
              this.error("Error acquiring handle:", e);
              this.releaseAccessHandles();
              throw e;
            }
          }
        })
      );
      const locked = results.filter(
        (r) => r.status === "rejected" && r.reason?.name === "NoModificationAllowedError"
      );
      if (locked.length === 0 || attempt === maxRetries - 1) {
        if (locked.length > 0) {
          this.warn(`${locked.length} files still locked after ${maxRetries} attempts, deleting...`);
          for (let i = 0; i < files.length; i++) {
            if (results[i].status === "rejected" && results[i].reason?.name === "NoModificationAllowedError") {
              const [name] = files[i];
              try {
                await this.#dhOpaque.removeEntry(name);
                this.log(`Deleted locked file: ${name}`);
              } catch (deleteError) {
                this.warn(`Could not delete locked file: ${name}`, deleteError);
              }
            }
          }
        }
        if (this.getCapacity() === 0 && files.length > 0) {
          throw new Error(`Failed to acquire any access handles from ${files.length} files`);
        }
        break;
      }
      this.#mapSAHToName.clear();
      this.#mapFilenameToSAH.clear();
      this.#availableSAH.clear();
    }
  }
  /**
   * Get associated path from SAH header
   */
  getAssociatedPath(sah) {
    sah.read(this.#apBody, { at: 0 });
    const flags = this.#dvBody.getUint32(HEADER_OFFSET_FLAGS);
    if (this.#apBody[0] && (flags & SQLITE_OPEN_DELETEONCLOSE || (flags & PERSISTENT_FILE_TYPES) === 0)) {
      this.warn(`Removing file with unexpected flags ${flags.toString(16)}`);
      this.setAssociatedPath(sah, "", 0);
      return "";
    }
    const fileDigest = new Uint32Array(HEADER_DIGEST_SIZE / 4);
    sah.read(fileDigest, { at: HEADER_OFFSET_DIGEST });
    const compDigest = this.computeDigest(this.#apBody, flags);
    if (fileDigest.every((v, i) => v === compDigest[i])) {
      const pathBytes = this.#apBody.findIndex((v) => 0 === v);
      if (0 === pathBytes) {
        sah.truncate(HEADER_OFFSET_DATA);
        return "";
      }
      return textDecoder.decode(this.#apBody.subarray(0, pathBytes));
    } else {
      this.warn("Disassociating file with bad digest");
      this.setAssociatedPath(sah, "", 0);
      return "";
    }
  }
  /**
   * Set associated path in SAH header
   */
  setAssociatedPath(sah, path, flags) {
    const enc = textEncoder.encodeInto(path, this.#apBody);
    if (HEADER_MAX_PATH_SIZE <= enc.written + 1) {
      throw new Error(`Path too long: ${path}`);
    }
    if (path && flags) {
      flags |= FLAG_COMPUTE_DIGEST_V2;
    }
    this.#apBody.fill(0, enc.written, HEADER_MAX_PATH_SIZE);
    this.#dvBody.setUint32(HEADER_OFFSET_FLAGS, flags);
    const digest = this.computeDigest(this.#apBody, flags);
    sah.write(this.#apBody, { at: 0 });
    sah.write(digest, { at: HEADER_OFFSET_DIGEST });
    sah.flush();
    if (path) {
      this.#mapFilenameToSAH.set(path, sah);
      this.#availableSAH.delete(sah);
    } else {
      sah.truncate(HEADER_OFFSET_DATA);
      this.#availableSAH.add(sah);
    }
  }
  /**
   * Compute digest for file header (cyrb53-inspired hash)
   */
  computeDigest(byteArray, fileFlags) {
    if (fileFlags & FLAG_COMPUTE_DIGEST_V2) {
      let h1 = 3735928559;
      let h2 = 1103547991;
      for (const v of byteArray) {
        h1 = Math.imul(h1 ^ v, 2654435761);
        h2 = Math.imul(h2 ^ v, 104729);
      }
      return new Uint32Array([h1 >>> 0, h2 >>> 0]);
    } else {
      return new Uint32Array([0, 0]);
    }
  }
  /**
   * Reset/initialize the pool
   */
  async reset(clearFiles) {
    let h = await navigator.storage.getDirectory();
    let prev;
    for (const d of this.vfsDir.split("/")) {
      if (d) {
        prev = h;
        h = await h.getDirectoryHandle(d, { create: true });
      }
    }
    this.#dhVfsRoot = h;
    this.#dhVfsParent = prev;
    this.#dhOpaque = await this.#dhVfsRoot.getDirectoryHandle(OPAQUE_DIR_NAME, {
      create: true
    });
    this.releaseAccessHandles();
    return this.acquireAccessHandles(clearFiles);
  }
  /**
   * Get path (handle both string and pointer)
   */
  getPath(arg) {
    if (typeof arg === "string") {
      return new URL(arg, "file://localhost/").pathname;
    }
    return arg;
  }
  /**
   * Check if filename exists
   */
  hasFilename(name) {
    return this.#mapFilenameToSAH.has(name);
  }
  /**
   * Get SAH for path
   */
  getSAHForPath(path) {
    return this.#mapFilenameToSAH.get(path);
  }
  /**
   * Get next available SAH
   */
  nextAvailableSAH() {
    const [rc] = this.#availableSAH.keys();
    return rc;
  }
  /**
   * Delete path association
   */
  deletePath(path) {
    const sah = this.#mapFilenameToSAH.get(path);
    if (sah) {
      this.#mapFilenameToSAH.delete(path);
      this.setAssociatedPath(sah, "", 0);
      return true;
    }
    return false;
  }
  // ===== VFS Methods (called from EM_JS hooks) =====
  /**
   * Open a file - returns file ID
   */
  xOpen(filename, flags) {
    try {
      const path = this.getPath(filename);
      this.log(`xOpen: ${path} flags=${flags}`);
      let sah = this.getSAHForPath(path);
      if (!sah && flags & SQLITE_OPEN_CREATE) {
        if (this.getFileCount() < this.getCapacity()) {
          sah = this.nextAvailableSAH();
          if (sah) {
            this.setAssociatedPath(sah, path, flags);
          } else {
            this.error("No available SAH in pool");
            return -1;
          }
        } else {
          this.error("SAH pool is full, cannot create file");
          return -1;
        }
      }
      if (!sah) {
        this.error(`File not found: ${path}`);
        return -1;
      }
      const fileId = this.#nextFileId++;
      this.#mapFileIdToFile.set(fileId, {
        path,
        sah,
        flags,
        lockType: SQLITE_LOCK_NONE
      });
      this.log(`xOpen success: ${path} -> fileId ${fileId}`);
      return fileId;
    } catch (e) {
      this.error("xOpen error:", e);
      return -1;
    }
  }
  /**
   * Read from file
   */
  xRead(fileId, buffer, amount, offset) {
    try {
      const file = this.#mapFileIdToFile.get(fileId);
      if (!file) {
        this.error(`xRead: invalid fileId ${fileId}`);
        return SQLITE_IOERR_READ;
      }
      const nRead = file.sah.read(buffer, { at: HEADER_OFFSET_DATA + offset });
      if (nRead < amount) {
        buffer.fill(0, nRead);
        return SQLITE_IOERR_SHORT_READ;
      }
      return SQLITE_OK;
    } catch (e) {
      this.error("xRead error:", e);
      return SQLITE_IOERR_READ;
    }
  }
  /**
   * Write to file
   */
  xWrite(fileId, buffer, amount, offset) {
    try {
      const file = this.#mapFileIdToFile.get(fileId);
      if (!file) {
        this.error(`xWrite: invalid fileId ${fileId}`);
        return SQLITE_IOERR_WRITE;
      }
      const nWritten = file.sah.write(buffer, { at: HEADER_OFFSET_DATA + offset });
      if (nWritten !== amount) {
        this.error(`xWrite: wrote ${nWritten}/${amount} bytes`);
        return SQLITE_IOERR_WRITE;
      }
      return SQLITE_OK;
    } catch (e) {
      this.error("xWrite error:", e);
      return SQLITE_IOERR_WRITE;
    }
  }
  /**
   * Sync file to storage
   */
  xSync(fileId, flags) {
    try {
      const file = this.#mapFileIdToFile.get(fileId);
      if (!file) {
        return SQLITE_IOERR;
      }
      file.sah.flush();
      return SQLITE_OK;
    } catch (e) {
      this.error("xSync error:", e);
      return SQLITE_IOERR;
    }
  }
  /**
   * Truncate file
   */
  xTruncate(fileId, size) {
    try {
      const file = this.#mapFileIdToFile.get(fileId);
      if (!file) {
        return SQLITE_IOERR;
      }
      file.sah.truncate(HEADER_OFFSET_DATA + size);
      return SQLITE_OK;
    } catch (e) {
      this.error("xTruncate error:", e);
      return SQLITE_IOERR;
    }
  }
  /**
   * Get file size
   */
  xFileSize(fileId) {
    try {
      const file = this.#mapFileIdToFile.get(fileId);
      if (!file) {
        return -1;
      }
      const size = file.sah.getSize() - HEADER_OFFSET_DATA;
      return Math.max(0, size);
    } catch (e) {
      this.error("xFileSize error:", e);
      return -1;
    }
  }
  /**
   * Close file
   */
  xClose(fileId) {
    try {
      const file = this.#mapFileIdToFile.get(fileId);
      if (!file) {
        return SQLITE_ERROR;
      }
      this.#mapFileIdToFile.delete(fileId);
      this.log(`xClose: fileId ${fileId} (${file.path})`);
      return SQLITE_OK;
    } catch (e) {
      this.error("xClose error:", e);
      return SQLITE_ERROR;
    }
  }
  /**
   * Check file access
   */
  xAccess(filename, flags) {
    try {
      const path = this.getPath(filename);
      return this.hasFilename(path) ? 1 : 0;
    } catch (e) {
      this.error("xAccess error:", e);
      return 0;
    }
  }
  /**
   * Delete file
   */
  xDelete(filename, syncDir) {
    try {
      const path = this.getPath(filename);
      this.log(`xDelete: ${path}`);
      this.deletePath(path);
      return SQLITE_OK;
    } catch (e) {
      this.error("xDelete error:", e);
      return SQLITE_IOERR;
    }
  }
};
var opfsSAHPool = new OpfsSAHPool({
  directory: ".opfs-sahpool",
  initialCapacity: 6,
  clearOnInit: false
});
if (typeof globalThis !== "undefined") {
  globalThis.opfsSAHPool = opfsSAHPool;
}

// opfs-worker.ts
opfsSAHPool.isReady.then(() => {
  console.log("[OPFS Worker] SAHPool initialized, sending ready signal");
  self.postMessage({ type: "ready" });
}).catch((error) => {
  console.error("[OPFS Worker] SAHPool initialization failed:", error);
  self.postMessage({ type: "error", error: error.message });
});
self.onmessage = async (event) => {
  const { id, type, args } = event.data;
  try {
    let result;
    switch (type) {
      case "cleanup":
        console.log("[OPFS Worker] Cleaning up handles before unload...");
        opfsSAHPool.releaseAccessHandles();
        console.log("[OPFS Worker] Cleanup complete");
        result = { success: true };
        break;
      case "getCapacity":
        result = {
          capacity: opfsSAHPool.getCapacity()
        };
        break;
      case "addCapacity":
        result = {
          newCapacity: await opfsSAHPool.addCapacity(args.count)
        };
        break;
      case "getFileList":
        result = {
          files: opfsSAHPool.getFileNames()
        };
        break;
      case "readFile":
        const fileId = opfsSAHPool.xOpen(args.filename, 1);
        if (fileId < 0) {
          throw new Error(`File not found: ${args.filename}`);
        }
        const size = opfsSAHPool.xFileSize(fileId);
        const buffer = new Uint8Array(size);
        const readResult = opfsSAHPool.xRead(fileId, buffer, size, 0);
        opfsSAHPool.xClose(fileId);
        if (readResult !== 0) {
          throw new Error(`Failed to read file: ${args.filename}`);
        }
        result = {
          data: Array.from(buffer)
        };
        break;
      case "writeFile":
        const data = new Uint8Array(args.data);
        const writeFileId = opfsSAHPool.xOpen(
          args.filename,
          2 | 4 | 256
          // READWRITE | CREATE | MAIN_DB
        );
        if (writeFileId < 0) {
          throw new Error(`Failed to open file for writing: ${args.filename}`);
        }
        opfsSAHPool.xTruncate(writeFileId, data.length);
        const writeResult = opfsSAHPool.xWrite(writeFileId, data, data.length, 0);
        opfsSAHPool.xSync(writeFileId, 0);
        opfsSAHPool.xClose(writeFileId);
        if (writeResult !== 0) {
          throw new Error(`Failed to write file: ${args.filename}`);
        }
        result = {
          bytesWritten: data.length
        };
        break;
      case "deleteFile":
        const deleteResult = opfsSAHPool.xDelete(args.filename, 1);
        if (deleteResult !== 0) {
          throw new Error(`Failed to delete file: ${args.filename}`);
        }
        result = { success: true };
        break;
      case "fileExists":
        const exists = opfsSAHPool.xAccess(args.filename, 0) === 0;
        result = { exists };
        break;
      default:
        throw new Error(`Unknown message type: ${type}`);
    }
    const response = {
      id,
      success: true,
      result
    };
    self.postMessage(response);
  } catch (error) {
    const response = {
      id,
      success: false,
      error: error instanceof Error ? error.message : "Unknown error"
    };
    self.postMessage(response);
  }
};
console.log("[OPFS Worker] Worker script loaded, waiting for SAHPool initialization...");
//# sourceMappingURL=data:application/json;base64,ewogICJ2ZXJzaW9uIjogMywKICAic291cmNlcyI6IFsiLi4vVHlwZXNjcmlwdC9vcGZzLXNhaHBvb2wudHMiLCAiLi4vVHlwZXNjcmlwdC9vcGZzLXdvcmtlci50cyJdLAogICJzb3VyY2VzQ29udGVudCI6IFsiLyoqXG4gKiBvcGZzLXNhaHBvb2wuanMgLSBTdGFuZGFsb25lIE9wZnNTQUhQb29sIFZGUyBJbXBsZW1lbnRhdGlvblxuICpcbiAqIEJhc2VkIG9uIEBzcWxpdGUub3JnL3NxbGl0ZS13YXNtJ3MgT3Bmc1NBSFBvb2wgYnkgdGhlIFNRTGl0ZSB0ZWFtXG4gKiBXaGljaCBpcyBiYXNlZCBvbiBSb3kgSGFzaGltb3RvJ3MgQWNjZXNzSGFuZGxlUG9vbFZGU1xuICpcbiAqIFRoaXMgaXMgYSBzaW1wbGlmaWVkIHZlcnNpb24gZm9yIGRpcmVjdCBpbnRlZ3JhdGlvbiB3aXRoIG91ciBjdXN0b20gZV9zcWxpdGUzX2pzdmZzLmFcbiAqIE5vIHdvcmtlciBtZXNzYWdpbmcgLSBydW5zIGRpcmVjdGx5IGluIHRoZSB3b3JrZXIgY29udGV4dCB3aXRoIG91ciBXQVNNIG1vZHVsZS5cbiAqL1xuXG4vLyBDb25zdGFudHMgbWF0Y2hpbmcgU1FMaXRlIFZGUyByZXF1aXJlbWVudHNcbmNvbnN0IFNFQ1RPUl9TSVpFID0gNDA5NjtcbmNvbnN0IEhFQURFUl9NQVhfUEFUSF9TSVpFID0gNTEyO1xuY29uc3QgSEVBREVSX0ZMQUdTX1NJWkUgPSA0O1xuY29uc3QgSEVBREVSX0RJR0VTVF9TSVpFID0gODtcbmNvbnN0IEhFQURFUl9DT1JQVVNfU0laRSA9IEhFQURFUl9NQVhfUEFUSF9TSVpFICsgSEVBREVSX0ZMQUdTX1NJWkU7XG5jb25zdCBIRUFERVJfT0ZGU0VUX0ZMQUdTID0gSEVBREVSX01BWF9QQVRIX1NJWkU7XG5jb25zdCBIRUFERVJfT0ZGU0VUX0RJR0VTVCA9IEhFQURFUl9DT1JQVVNfU0laRTtcbmNvbnN0IEhFQURFUl9PRkZTRVRfREFUQSA9IFNFQ1RPUl9TSVpFO1xuXG4vLyBTUUxpdGUgZmlsZSB0eXBlIGZsYWdzXG5jb25zdCBTUUxJVEVfT1BFTl9NQUlOX0RCID0gMHgwMDAwMDEwMDtcbmNvbnN0IFNRTElURV9PUEVOX01BSU5fSk9VUk5BTCA9IDB4MDAwMDA4MDA7XG5jb25zdCBTUUxJVEVfT1BFTl9TVVBFUl9KT1VSTkFMID0gMHgwMDAwNDAwMDtcbmNvbnN0IFNRTElURV9PUEVOX1dBTCA9IDB4MDAwODAwMDA7XG5jb25zdCBTUUxJVEVfT1BFTl9DUkVBVEUgPSAweDAwMDAwMDA0O1xuY29uc3QgU1FMSVRFX09QRU5fREVMRVRFT05DTE9TRSA9IDB4MDAwMDAwMDg7XG5jb25zdCBTUUxJVEVfT1BFTl9NRU1PUlkgPSAweDAwMDAwMDgwOyAvLyBVc2VkIGFzIEZMQUdfQ09NUFVURV9ESUdFU1RfVjJcblxuY29uc3QgUEVSU0lTVEVOVF9GSUxFX1RZUEVTID1cbiAgU1FMSVRFX09QRU5fTUFJTl9EQiB8XG4gIFNRTElURV9PUEVOX01BSU5fSk9VUk5BTCB8XG4gIFNRTElURV9PUEVOX1NVUEVSX0pPVVJOQUwgfFxuICBTUUxJVEVfT1BFTl9XQUw7XG5cbmNvbnN0IEZMQUdfQ09NUFVURV9ESUdFU1RfVjIgPSBTUUxJVEVfT1BFTl9NRU1PUlk7XG5jb25zdCBPUEFRVUVfRElSX05BTUUgPSAnLm9wYXF1ZSc7XG5cbi8vIFNRTGl0ZSByZXN1bHQgY29kZXNcbmNvbnN0IFNRTElURV9PSyA9IDA7XG5jb25zdCBTUUxJVEVfRVJST1IgPSAxO1xuY29uc3QgU1FMSVRFX0lPRVJSID0gMTA7XG5jb25zdCBTUUxJVEVfSU9FUlJfU0hPUlRfUkVBRCA9IDUyMjsgLy8gU1FMSVRFX0lPRVJSIHwgKDI8PDgpXG5jb25zdCBTUUxJVEVfSU9FUlJfV1JJVEUgPSA3Nzg7IC8vIFNRTElURV9JT0VSUiB8ICgzPDw4KVxuY29uc3QgU1FMSVRFX0lPRVJSX1JFQUQgPSAyNjY7IC8vIFNRTElURV9JT0VSUiB8ICgxPDw4KVxuY29uc3QgU1FMSVRFX0NBTlRPUEVOID0gMTQ7XG5jb25zdCBTUUxJVEVfTE9DS19OT05FID0gMDtcblxuY29uc3QgZ2V0UmFuZG9tTmFtZSA9ICgpID0+IE1hdGgucmFuZG9tKCkudG9TdHJpbmcoMzYpLnNsaWNlKDIpO1xuY29uc3QgdGV4dERlY29kZXIgPSBuZXcgVGV4dERlY29kZXIoKTtcbmNvbnN0IHRleHRFbmNvZGVyID0gbmV3IFRleHRFbmNvZGVyKCk7XG5cbi8qKlxuICogT3Bmc1NBSFBvb2wgLSBQb29sLWJhc2VkIE9QRlMgVkZTIHdpdGggU3luY2hyb25vdXMgQWNjZXNzIEhhbmRsZXNcbiAqXG4gKiBNYW5hZ2VzIGEgcG9vbCBvZiBwcmUtYWxsb2NhdGVkIE9QRlMgZmlsZXMgd2l0aCBzeW5jaHJvbm91cyBhY2Nlc3MgaGFuZGxlcy5cbiAqIEZpbGVzIGFyZSBzdG9yZWQgd2l0aCBhIDQwOTYtYnl0ZSBoZWFkZXIgY29udGFpbmluZyBtZXRhZGF0YS5cbiAqL1xuY2xhc3MgT3Bmc1NBSFBvb2wge1xuICAjZGhWZnNSb290ID0gbnVsbDtcbiAgI2RoT3BhcXVlID0gbnVsbDtcbiAgI2RoVmZzUGFyZW50ID0gbnVsbDtcblxuICAvLyBQb29sIG1hbmFnZW1lbnRcbiAgI21hcFNBSFRvTmFtZSA9IG5ldyBNYXAoKTsgICAgICAgLy8gU0FIIC0+IHJhbmRvbSBPUEZTIGZpbGVuYW1lXG4gICNtYXBGaWxlbmFtZVRvU0FIID0gbmV3IE1hcCgpOyAgIC8vIFNRTGl0ZSBwYXRoIC0+IFNBSFxuICAjYXZhaWxhYmxlU0FIID0gbmV3IFNldCgpOyAgICAgICAvLyBVbmFzc29jaWF0ZWQgU0FIcyByZWFkeSBmb3IgdXNlXG5cbiAgLy8gRmlsZSBoYW5kbGUgdHJhY2tpbmcgZm9yIG9wZW4gZmlsZXNcbiAgI21hcEZpbGVJZFRvRmlsZSA9IG5ldyBNYXAoKTsgICAgLy8gZmlsZUlkIC0+IHtwYXRoLCBzYWgsIGxvY2tUeXBlLCBmbGFnc31cbiAgI25leHRGaWxlSWQgPSAxO1xuXG4gIC8vIEhlYWRlciBidWZmZXIgZm9yIHJlYWRpbmcvd3JpdGluZyBmaWxlIG1ldGFkYXRhXG4gICNhcEJvZHkgPSBuZXcgVWludDhBcnJheShIRUFERVJfQ09SUFVTX1NJWkUpO1xuICAjZHZCb2R5ID0gbnVsbDtcblxuICB2ZnNEaXIgPSBudWxsO1xuXG4gIGNvbnN0cnVjdG9yKG9wdGlvbnMgPSB7fSkge1xuICAgIHRoaXMudmZzRGlyID0gb3B0aW9ucy5kaXJlY3RvcnkgfHwgJy5vcGZzLXNhaHBvb2wnO1xuICAgIHRoaXMuI2R2Qm9keSA9IG5ldyBEYXRhVmlldyh0aGlzLiNhcEJvZHkuYnVmZmVyLCB0aGlzLiNhcEJvZHkuYnl0ZU9mZnNldCk7XG4gICAgdGhpcy5pc1JlYWR5ID0gdGhpcy5yZXNldChvcHRpb25zLmNsZWFyT25Jbml0IHx8IGZhbHNlKVxuICAgICAgLnRoZW4oKCkgPT4ge1xuICAgICAgICBjb25zdCBjYXBhY2l0eSA9IHRoaXMuZ2V0Q2FwYWNpdHkoKTtcbiAgICAgICAgaWYgKGNhcGFjaXR5ID4gMCkge1xuICAgICAgICAgIHJldHVybiBQcm9taXNlLnJlc29sdmUoKTtcbiAgICAgICAgfVxuICAgICAgICByZXR1cm4gdGhpcy5hZGRDYXBhY2l0eShvcHRpb25zLmluaXRpYWxDYXBhY2l0eSB8fCA2KTtcbiAgICAgIH0pO1xuICB9XG5cbiAgbG9nKC4uLmFyZ3MpIHtcbiAgICBjb25zb2xlLmxvZygnW09wZnNTQUhQb29sXScsIC4uLmFyZ3MpO1xuICB9XG5cbiAgd2FybiguLi5hcmdzKSB7XG4gICAgY29uc29sZS53YXJuKCdbT3Bmc1NBSFBvb2xdJywgLi4uYXJncyk7XG4gIH1cblxuICBlcnJvciguLi5hcmdzKSB7XG4gICAgY29uc29sZS5lcnJvcignW09wZnNTQUhQb29sXScsIC4uLmFyZ3MpO1xuICB9XG5cbiAgZ2V0Q2FwYWNpdHkoKSB7XG4gICAgcmV0dXJuIHRoaXMuI21hcFNBSFRvTmFtZS5zaXplO1xuICB9XG5cbiAgZ2V0RmlsZUNvdW50KCkge1xuICAgIHJldHVybiB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILnNpemU7XG4gIH1cblxuICBnZXRGaWxlTmFtZXMoKSB7XG4gICAgcmV0dXJuIEFycmF5LmZyb20odGhpcy4jbWFwRmlsZW5hbWVUb1NBSC5rZXlzKCkpO1xuICB9XG5cbiAgLyoqXG4gICAqIEFkZCBjYXBhY2l0eSAtIGNyZWF0ZSBuIG5ldyBPUEZTIGZpbGVzIHdpdGggc3luYyBhY2Nlc3MgaGFuZGxlc1xuICAgKi9cbiAgYXN5bmMgYWRkQ2FwYWNpdHkobikge1xuICAgIGZvciAobGV0IGkgPSAwOyBpIDwgbjsgKytpKSB7XG4gICAgICBjb25zdCBuYW1lID0gZ2V0UmFuZG9tTmFtZSgpO1xuICAgICAgY29uc3QgaCA9IGF3YWl0IHRoaXMuI2RoT3BhcXVlLmdldEZpbGVIYW5kbGUobmFtZSwgeyBjcmVhdGU6IHRydWUgfSk7XG4gICAgICBjb25zdCBhaCA9IGF3YWl0IGguY3JlYXRlU3luY0FjY2Vzc0hhbmRsZSgpO1xuICAgICAgdGhpcy4jbWFwU0FIVG9OYW1lLnNldChhaCwgbmFtZSk7XG4gICAgICB0aGlzLnNldEFzc29jaWF0ZWRQYXRoKGFoLCAnJywgMCk7XG4gICAgfVxuICAgIHRoaXMubG9nKGBBZGRlZCAke259IGhhbmRsZXMsIHRvdGFsIGNhcGFjaXR5OiAke3RoaXMuZ2V0Q2FwYWNpdHkoKX1gKTtcbiAgICByZXR1cm4gdGhpcy5nZXRDYXBhY2l0eSgpO1xuICB9XG5cbiAgLyoqXG4gICAqIFJlbGVhc2UgYWxsIGFjY2VzcyBoYW5kbGVzIChjbGVhbnVwKVxuICAgKi9cbiAgcmVsZWFzZUFjY2Vzc0hhbmRsZXMoKSB7XG4gICAgZm9yIChjb25zdCBhaCBvZiB0aGlzLiNtYXBTQUhUb05hbWUua2V5cygpKSB7XG4gICAgICB0cnkge1xuICAgICAgICBhaC5jbG9zZSgpO1xuICAgICAgfSBjYXRjaCAoZSkge1xuICAgICAgICB0aGlzLndhcm4oJ0Vycm9yIGNsb3NpbmcgaGFuZGxlOicsIGUpO1xuICAgICAgfVxuICAgIH1cbiAgICB0aGlzLiNtYXBTQUhUb05hbWUuY2xlYXIoKTtcbiAgICB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILmNsZWFyKCk7XG4gICAgdGhpcy4jYXZhaWxhYmxlU0FILmNsZWFyKCk7XG4gICAgdGhpcy4jbWFwRmlsZUlkVG9GaWxlLmNsZWFyKCk7XG4gIH1cblxuICAvKipcbiAgICogQWNxdWlyZSBhbGwgZXhpc3RpbmcgYWNjZXNzIGhhbmRsZXMgZnJvbSBPUEZTIGRpcmVjdG9yeSB3aXRoIHJldHJ5IGxvZ2ljXG4gICAqL1xuICBhc3luYyBhY3F1aXJlQWNjZXNzSGFuZGxlcyhjbGVhckZpbGVzID0gZmFsc2UpIHtcbiAgICBjb25zdCBmaWxlcyA9IFtdO1xuICAgIGZvciBhd2FpdCAoY29uc3QgW25hbWUsIGhdIG9mIHRoaXMuI2RoT3BhcXVlKSB7XG4gICAgICBpZiAoJ2ZpbGUnID09PSBoLmtpbmQpIHtcbiAgICAgICAgZmlsZXMucHVzaChbbmFtZSwgaF0pO1xuICAgICAgfVxuICAgIH1cblxuICAgIC8vIFRyeSB0byBhY3F1aXJlIGhhbmRsZXMgd2l0aCByZXRyaWVzIHRvIGFsbG93IEdDIHRvIHJlbGVhc2Ugb2xkIGhhbmRsZXNcbiAgICBjb25zdCBtYXhSZXRyaWVzID0gMztcbiAgICBjb25zdCByZXRyeURlbGF5ID0gMTAwOyAvLyBtc1xuXG4gICAgZm9yIChsZXQgYXR0ZW1wdCA9IDA7IGF0dGVtcHQgPCBtYXhSZXRyaWVzOyBhdHRlbXB0KyspIHtcbiAgICAgIGlmIChhdHRlbXB0ID4gMCkge1xuICAgICAgICB0aGlzLndhcm4oYFJldHJ5ICR7YXR0ZW1wdH0vJHttYXhSZXRyaWVzIC0gMX0gYWZ0ZXIgJHtyZXRyeURlbGF5fW1zIGRlbGF5Li4uYCk7XG4gICAgICAgIGF3YWl0IG5ldyBQcm9taXNlKHJlc29sdmUgPT4gc2V0VGltZW91dChyZXNvbHZlLCByZXRyeURlbGF5ICogYXR0ZW1wdCkpO1xuICAgICAgfVxuXG4gICAgICBjb25zdCByZXN1bHRzID0gYXdhaXQgUHJvbWlzZS5hbGxTZXR0bGVkKFxuICAgICAgICBmaWxlcy5tYXAoYXN5bmMgKFtuYW1lLCBoXSkgPT4ge1xuICAgICAgICAgIHRyeSB7XG4gICAgICAgICAgICBjb25zdCBhaCA9IGF3YWl0IGguY3JlYXRlU3luY0FjY2Vzc0hhbmRsZSgpO1xuICAgICAgICAgICAgdGhpcy4jbWFwU0FIVG9OYW1lLnNldChhaCwgbmFtZSk7XG5cbiAgICAgICAgICAgIGlmIChjbGVhckZpbGVzKSB7XG4gICAgICAgICAgICAgIGFoLnRydW5jYXRlKEhFQURFUl9PRkZTRVRfREFUQSk7XG4gICAgICAgICAgICAgIHRoaXMuc2V0QXNzb2NpYXRlZFBhdGgoYWgsICcnLCAwKTtcbiAgICAgICAgICAgIH0gZWxzZSB7XG4gICAgICAgICAgICAgIGNvbnN0IHBhdGggPSB0aGlzLmdldEFzc29jaWF0ZWRQYXRoKGFoKTtcbiAgICAgICAgICAgICAgaWYgKHBhdGgpIHtcbiAgICAgICAgICAgICAgICB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILnNldChwYXRoLCBhaCk7XG4gICAgICAgICAgICAgICAgdGhpcy5sb2coYFJlc3RvcmVkIGZpbGUgYXNzb2NpYXRpb246ICR7cGF0aH0gLT4gJHtuYW1lfWApO1xuICAgICAgICAgICAgICB9IGVsc2Uge1xuICAgICAgICAgICAgICAgIHRoaXMuI2F2YWlsYWJsZVNBSC5hZGQoYWgpO1xuICAgICAgICAgICAgICB9XG4gICAgICAgICAgICB9XG4gICAgICAgICAgfSBjYXRjaCAoZSkge1xuICAgICAgICAgICAgaWYgKGUubmFtZSA9PT0gJ05vTW9kaWZpY2F0aW9uQWxsb3dlZEVycm9yJykge1xuICAgICAgICAgICAgICAvLyBGaWxlIGlzIGxvY2tlZCAtIHdpbGwgcmV0cnkgb3IgZGVsZXRlIG9uIGxhc3QgYXR0ZW1wdFxuICAgICAgICAgICAgICB0aHJvdyBlO1xuICAgICAgICAgICAgfSBlbHNlIHtcbiAgICAgICAgICAgICAgdGhpcy5lcnJvcignRXJyb3IgYWNxdWlyaW5nIGhhbmRsZTonLCBlKTtcbiAgICAgICAgICAgICAgdGhpcy5yZWxlYXNlQWNjZXNzSGFuZGxlcygpO1xuICAgICAgICAgICAgICB0aHJvdyBlO1xuICAgICAgICAgICAgfVxuICAgICAgICAgIH1cbiAgICAgICAgfSlcbiAgICAgICk7XG5cbiAgICAgIGNvbnN0IGxvY2tlZCA9IHJlc3VsdHMuZmlsdGVyKHIgPT5cbiAgICAgICAgci5zdGF0dXMgPT09ICdyZWplY3RlZCcgJiZcbiAgICAgICAgci5yZWFzb24/Lm5hbWUgPT09ICdOb01vZGlmaWNhdGlvbkFsbG93ZWRFcnJvcidcbiAgICAgICk7XG5cbiAgICAgIC8vIElmIHdlIGFjcXVpcmVkIHNvbWUgaGFuZGxlcyBvciB0aGlzIGlzIHRoZSBsYXN0IGF0dGVtcHQsIGRlY2lkZSB3aGF0IHRvIGRvXG4gICAgICBpZiAobG9ja2VkLmxlbmd0aCA9PT0gMCB8fCBhdHRlbXB0ID09PSBtYXhSZXRyaWVzIC0gMSkge1xuICAgICAgICBpZiAobG9ja2VkLmxlbmd0aCA+IDApIHtcbiAgICAgICAgICAvLyBMYXN0IGF0dGVtcHQgLSBkZWxldGUgbG9ja2VkIGZpbGVzIGFzIGxhc3QgcmVzb3J0XG4gICAgICAgICAgdGhpcy53YXJuKGAke2xvY2tlZC5sZW5ndGh9IGZpbGVzIHN0aWxsIGxvY2tlZCBhZnRlciAke21heFJldHJpZXN9IGF0dGVtcHRzLCBkZWxldGluZy4uLmApO1xuICAgICAgICAgIGZvciAobGV0IGkgPSAwOyBpIDwgZmlsZXMubGVuZ3RoOyBpKyspIHtcbiAgICAgICAgICAgIGlmIChyZXN1bHRzW2ldLnN0YXR1cyA9PT0gJ3JlamVjdGVkJyAmJiByZXN1bHRzW2ldLnJlYXNvbj8ubmFtZSA9PT0gJ05vTW9kaWZpY2F0aW9uQWxsb3dlZEVycm9yJykge1xuICAgICAgICAgICAgICBjb25zdCBbbmFtZV0gPSBmaWxlc1tpXTtcbiAgICAgICAgICAgICAgdHJ5IHtcbiAgICAgICAgICAgICAgICBhd2FpdCB0aGlzLiNkaE9wYXF1ZS5yZW1vdmVFbnRyeShuYW1lKTtcbiAgICAgICAgICAgICAgICB0aGlzLmxvZyhgRGVsZXRlZCBsb2NrZWQgZmlsZTogJHtuYW1lfWApO1xuICAgICAgICAgICAgICB9IGNhdGNoIChkZWxldGVFcnJvcikge1xuICAgICAgICAgICAgICAgIHRoaXMud2FybihgQ291bGQgbm90IGRlbGV0ZSBsb2NrZWQgZmlsZTogJHtuYW1lfWAsIGRlbGV0ZUVycm9yKTtcbiAgICAgICAgICAgICAgfVxuICAgICAgICAgICAgfVxuICAgICAgICAgIH1cbiAgICAgICAgfVxuXG4gICAgICAgIC8vIENoZWNrIGlmIHdlIGhhdmUgYW55IGNhcGFjaXR5IGFmdGVyIGFsbCBhdHRlbXB0c1xuICAgICAgICBpZiAodGhpcy5nZXRDYXBhY2l0eSgpID09PSAwICYmIGZpbGVzLmxlbmd0aCA+IDApIHtcbiAgICAgICAgICB0aHJvdyBuZXcgRXJyb3IoYEZhaWxlZCB0byBhY3F1aXJlIGFueSBhY2Nlc3MgaGFuZGxlcyBmcm9tICR7ZmlsZXMubGVuZ3RofSBmaWxlc2ApO1xuICAgICAgICB9XG5cbiAgICAgICAgYnJlYWs7IC8vIEV4aXQgcmV0cnkgbG9vcFxuICAgICAgfVxuXG4gICAgICAvLyBDbGVhciBtYXBzIGZvciBuZXh0IHJldHJ5XG4gICAgICB0aGlzLiNtYXBTQUhUb05hbWUuY2xlYXIoKTtcbiAgICAgIHRoaXMuI21hcEZpbGVuYW1lVG9TQUguY2xlYXIoKTtcbiAgICAgIHRoaXMuI2F2YWlsYWJsZVNBSC5jbGVhcigpO1xuICAgIH1cbiAgfVxuXG4gIC8qKlxuICAgKiBHZXQgYXNzb2NpYXRlZCBwYXRoIGZyb20gU0FIIGhlYWRlclxuICAgKi9cbiAgZ2V0QXNzb2NpYXRlZFBhdGgoc2FoKSB7XG4gICAgc2FoLnJlYWQodGhpcy4jYXBCb2R5LCB7IGF0OiAwIH0pO1xuXG4gICAgY29uc3QgZmxhZ3MgPSB0aGlzLiNkdkJvZHkuZ2V0VWludDMyKEhFQURFUl9PRkZTRVRfRkxBR1MpO1xuXG4gICAgLy8gQ2hlY2sgaWYgZmlsZSBzaG91bGQgYmUgZGVsZXRlZFxuICAgIGlmIChcbiAgICAgIHRoaXMuI2FwQm9keVswXSAmJlxuICAgICAgKGZsYWdzICYgU1FMSVRFX09QRU5fREVMRVRFT05DTE9TRSB8fFxuICAgICAgICAoZmxhZ3MgJiBQRVJTSVNURU5UX0ZJTEVfVFlQRVMpID09PSAwKVxuICAgICkge1xuICAgICAgdGhpcy53YXJuKGBSZW1vdmluZyBmaWxlIHdpdGggdW5leHBlY3RlZCBmbGFncyAke2ZsYWdzLnRvU3RyaW5nKDE2KX1gKTtcbiAgICAgIHRoaXMuc2V0QXNzb2NpYXRlZFBhdGgoc2FoLCAnJywgMCk7XG4gICAgICByZXR1cm4gJyc7XG4gICAgfVxuXG4gICAgLy8gVmVyaWZ5IGRpZ2VzdFxuICAgIGNvbnN0IGZpbGVEaWdlc3QgPSBuZXcgVWludDMyQXJyYXkoSEVBREVSX0RJR0VTVF9TSVpFIC8gNCk7XG4gICAgc2FoLnJlYWQoZmlsZURpZ2VzdCwgeyBhdDogSEVBREVSX09GRlNFVF9ESUdFU1QgfSk7XG4gICAgY29uc3QgY29tcERpZ2VzdCA9IHRoaXMuY29tcHV0ZURpZ2VzdCh0aGlzLiNhcEJvZHksIGZsYWdzKTtcblxuICAgIGlmIChmaWxlRGlnZXN0LmV2ZXJ5KCh2LCBpKSA9PiB2ID09PSBjb21wRGlnZXN0W2ldKSkge1xuICAgICAgY29uc3QgcGF0aEJ5dGVzID0gdGhpcy4jYXBCb2R5LmZpbmRJbmRleCgodikgPT4gMCA9PT0gdik7XG4gICAgICBpZiAoMCA9PT0gcGF0aEJ5dGVzKSB7XG4gICAgICAgIHNhaC50cnVuY2F0ZShIRUFERVJfT0ZGU0VUX0RBVEEpO1xuICAgICAgICByZXR1cm4gJyc7XG4gICAgICB9XG4gICAgICByZXR1cm4gdGV4dERlY29kZXIuZGVjb2RlKHRoaXMuI2FwQm9keS5zdWJhcnJheSgwLCBwYXRoQnl0ZXMpKTtcbiAgICB9IGVsc2Uge1xuICAgICAgdGhpcy53YXJuKCdEaXNhc3NvY2lhdGluZyBmaWxlIHdpdGggYmFkIGRpZ2VzdCcpO1xuICAgICAgdGhpcy5zZXRBc3NvY2lhdGVkUGF0aChzYWgsICcnLCAwKTtcbiAgICAgIHJldHVybiAnJztcbiAgICB9XG4gIH1cblxuICAvKipcbiAgICogU2V0IGFzc29jaWF0ZWQgcGF0aCBpbiBTQUggaGVhZGVyXG4gICAqL1xuICBzZXRBc3NvY2lhdGVkUGF0aChzYWgsIHBhdGgsIGZsYWdzKSB7XG4gICAgY29uc3QgZW5jID0gdGV4dEVuY29kZXIuZW5jb2RlSW50byhwYXRoLCB0aGlzLiNhcEJvZHkpO1xuICAgIGlmIChIRUFERVJfTUFYX1BBVEhfU0laRSA8PSBlbmMud3JpdHRlbiArIDEpIHtcbiAgICAgIHRocm93IG5ldyBFcnJvcihgUGF0aCB0b28gbG9uZzogJHtwYXRofWApO1xuICAgIH1cblxuICAgIGlmIChwYXRoICYmIGZsYWdzKSB7XG4gICAgICBmbGFncyB8PSBGTEFHX0NPTVBVVEVfRElHRVNUX1YyO1xuICAgIH1cblxuICAgIHRoaXMuI2FwQm9keS5maWxsKDAsIGVuYy53cml0dGVuLCBIRUFERVJfTUFYX1BBVEhfU0laRSk7XG4gICAgdGhpcy4jZHZCb2R5LnNldFVpbnQzMihIRUFERVJfT0ZGU0VUX0ZMQUdTLCBmbGFncyk7XG4gICAgY29uc3QgZGlnZXN0ID0gdGhpcy5jb21wdXRlRGlnZXN0KHRoaXMuI2FwQm9keSwgZmxhZ3MpO1xuXG4gICAgc2FoLndyaXRlKHRoaXMuI2FwQm9keSwgeyBhdDogMCB9KTtcbiAgICBzYWgud3JpdGUoZGlnZXN0LCB7IGF0OiBIRUFERVJfT0ZGU0VUX0RJR0VTVCB9KTtcbiAgICBzYWguZmx1c2goKTtcblxuICAgIGlmIChwYXRoKSB7XG4gICAgICB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILnNldChwYXRoLCBzYWgpO1xuICAgICAgdGhpcy4jYXZhaWxhYmxlU0FILmRlbGV0ZShzYWgpO1xuICAgIH0gZWxzZSB7XG4gICAgICBzYWgudHJ1bmNhdGUoSEVBREVSX09GRlNFVF9EQVRBKTtcbiAgICAgIHRoaXMuI2F2YWlsYWJsZVNBSC5hZGQoc2FoKTtcbiAgICB9XG4gIH1cblxuICAvKipcbiAgICogQ29tcHV0ZSBkaWdlc3QgZm9yIGZpbGUgaGVhZGVyIChjeXJiNTMtaW5zcGlyZWQgaGFzaClcbiAgICovXG4gIGNvbXB1dGVEaWdlc3QoYnl0ZUFycmF5LCBmaWxlRmxhZ3MpIHtcbiAgICBpZiAoZmlsZUZsYWdzICYgRkxBR19DT01QVVRFX0RJR0VTVF9WMikge1xuICAgICAgbGV0IGgxID0gMHhkZWFkYmVlZjtcbiAgICAgIGxldCBoMiA9IDB4NDFjNmNlNTc7XG4gICAgICBmb3IgKGNvbnN0IHYgb2YgYnl0ZUFycmF5KSB7XG4gICAgICAgIGgxID0gTWF0aC5pbXVsKGgxIF4gdiwgMjY1NDQzNTc2MSk7XG4gICAgICAgIGgyID0gTWF0aC5pbXVsKGgyIF4gdiwgMTA0NzI5KTtcbiAgICAgIH1cbiAgICAgIHJldHVybiBuZXcgVWludDMyQXJyYXkoW2gxID4+PiAwLCBoMiA+Pj4gMF0pO1xuICAgIH0gZWxzZSB7XG4gICAgICByZXR1cm4gbmV3IFVpbnQzMkFycmF5KFswLCAwXSk7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIFJlc2V0L2luaXRpYWxpemUgdGhlIHBvb2xcbiAgICovXG4gIGFzeW5jIHJlc2V0KGNsZWFyRmlsZXMpIHtcbiAgICBsZXQgaCA9IGF3YWl0IG5hdmlnYXRvci5zdG9yYWdlLmdldERpcmVjdG9yeSgpO1xuICAgIGxldCBwcmV2O1xuXG4gICAgZm9yIChjb25zdCBkIG9mIHRoaXMudmZzRGlyLnNwbGl0KCcvJykpIHtcbiAgICAgIGlmIChkKSB7XG4gICAgICAgIHByZXYgPSBoO1xuICAgICAgICBoID0gYXdhaXQgaC5nZXREaXJlY3RvcnlIYW5kbGUoZCwgeyBjcmVhdGU6IHRydWUgfSk7XG4gICAgICB9XG4gICAgfVxuXG4gICAgdGhpcy4jZGhWZnNSb290ID0gaDtcbiAgICB0aGlzLiNkaFZmc1BhcmVudCA9IHByZXY7XG4gICAgdGhpcy4jZGhPcGFxdWUgPSBhd2FpdCB0aGlzLiNkaFZmc1Jvb3QuZ2V0RGlyZWN0b3J5SGFuZGxlKE9QQVFVRV9ESVJfTkFNRSwge1xuICAgICAgY3JlYXRlOiB0cnVlLFxuICAgIH0pO1xuXG4gICAgdGhpcy5yZWxlYXNlQWNjZXNzSGFuZGxlcygpO1xuICAgIHJldHVybiB0aGlzLmFjcXVpcmVBY2Nlc3NIYW5kbGVzKGNsZWFyRmlsZXMpO1xuICB9XG5cbiAgLyoqXG4gICAqIEdldCBwYXRoIChoYW5kbGUgYm90aCBzdHJpbmcgYW5kIHBvaW50ZXIpXG4gICAqL1xuICBnZXRQYXRoKGFyZykge1xuICAgIGlmICh0eXBlb2YgYXJnID09PSAnc3RyaW5nJykge1xuICAgICAgcmV0dXJuIG5ldyBVUkwoYXJnLCAnZmlsZTovL2xvY2FsaG9zdC8nKS5wYXRobmFtZTtcbiAgICB9XG4gICAgcmV0dXJuIGFyZztcbiAgfVxuXG4gIC8qKlxuICAgKiBDaGVjayBpZiBmaWxlbmFtZSBleGlzdHNcbiAgICovXG4gIGhhc0ZpbGVuYW1lKG5hbWUpIHtcbiAgICByZXR1cm4gdGhpcy4jbWFwRmlsZW5hbWVUb1NBSC5oYXMobmFtZSk7XG4gIH1cblxuICAvKipcbiAgICogR2V0IFNBSCBmb3IgcGF0aFxuICAgKi9cbiAgZ2V0U0FIRm9yUGF0aChwYXRoKSB7XG4gICAgcmV0dXJuIHRoaXMuI21hcEZpbGVuYW1lVG9TQUguZ2V0KHBhdGgpO1xuICB9XG5cbiAgLyoqXG4gICAqIEdldCBuZXh0IGF2YWlsYWJsZSBTQUhcbiAgICovXG4gIG5leHRBdmFpbGFibGVTQUgoKSB7XG4gICAgY29uc3QgW3JjXSA9IHRoaXMuI2F2YWlsYWJsZVNBSC5rZXlzKCk7XG4gICAgcmV0dXJuIHJjO1xuICB9XG5cbiAgLyoqXG4gICAqIERlbGV0ZSBwYXRoIGFzc29jaWF0aW9uXG4gICAqL1xuICBkZWxldGVQYXRoKHBhdGgpIHtcbiAgICBjb25zdCBzYWggPSB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILmdldChwYXRoKTtcbiAgICBpZiAoc2FoKSB7XG4gICAgICB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILmRlbGV0ZShwYXRoKTtcbiAgICAgIHRoaXMuc2V0QXNzb2NpYXRlZFBhdGgoc2FoLCAnJywgMCk7XG4gICAgICByZXR1cm4gdHJ1ZTtcbiAgICB9XG4gICAgcmV0dXJuIGZhbHNlO1xuICB9XG5cbiAgLy8gPT09PT0gVkZTIE1ldGhvZHMgKGNhbGxlZCBmcm9tIEVNX0pTIGhvb2tzKSA9PT09PVxuXG4gIC8qKlxuICAgKiBPcGVuIGEgZmlsZSAtIHJldHVybnMgZmlsZSBJRFxuICAgKi9cbiAgeE9wZW4oZmlsZW5hbWUsIGZsYWdzKSB7XG4gICAgdHJ5IHtcbiAgICAgIGNvbnN0IHBhdGggPSB0aGlzLmdldFBhdGgoZmlsZW5hbWUpO1xuICAgICAgdGhpcy5sb2coYHhPcGVuOiAke3BhdGh9IGZsYWdzPSR7ZmxhZ3N9YCk7XG5cbiAgICAgIGxldCBzYWggPSB0aGlzLmdldFNBSEZvclBhdGgocGF0aCk7XG5cbiAgICAgIGlmICghc2FoICYmIChmbGFncyAmIFNRTElURV9PUEVOX0NSRUFURSkpIHtcbiAgICAgICAgaWYgKHRoaXMuZ2V0RmlsZUNvdW50KCkgPCB0aGlzLmdldENhcGFjaXR5KCkpIHtcbiAgICAgICAgICBzYWggPSB0aGlzLm5leHRBdmFpbGFibGVTQUgoKTtcbiAgICAgICAgICBpZiAoc2FoKSB7XG4gICAgICAgICAgICB0aGlzLnNldEFzc29jaWF0ZWRQYXRoKHNhaCwgcGF0aCwgZmxhZ3MpO1xuICAgICAgICAgIH0gZWxzZSB7XG4gICAgICAgICAgICB0aGlzLmVycm9yKCdObyBhdmFpbGFibGUgU0FIIGluIHBvb2wnKTtcbiAgICAgICAgICAgIHJldHVybiAtMTtcbiAgICAgICAgICB9XG4gICAgICAgIH0gZWxzZSB7XG4gICAgICAgICAgdGhpcy5lcnJvcignU0FIIHBvb2wgaXMgZnVsbCwgY2Fubm90IGNyZWF0ZSBmaWxlJyk7XG4gICAgICAgICAgcmV0dXJuIC0xO1xuICAgICAgICB9XG4gICAgICB9XG5cbiAgICAgIGlmICghc2FoKSB7XG4gICAgICAgIHRoaXMuZXJyb3IoYEZpbGUgbm90IGZvdW5kOiAke3BhdGh9YCk7XG4gICAgICAgIHJldHVybiAtMTtcbiAgICAgIH1cblxuICAgICAgLy8gQWxsb2NhdGUgZmlsZSBJRFxuICAgICAgY29uc3QgZmlsZUlkID0gdGhpcy4jbmV4dEZpbGVJZCsrO1xuICAgICAgdGhpcy4jbWFwRmlsZUlkVG9GaWxlLnNldChmaWxlSWQsIHtcbiAgICAgICAgcGF0aCxcbiAgICAgICAgc2FoLFxuICAgICAgICBmbGFncyxcbiAgICAgICAgbG9ja1R5cGU6IFNRTElURV9MT0NLX05PTkVcbiAgICAgIH0pO1xuXG4gICAgICB0aGlzLmxvZyhgeE9wZW4gc3VjY2VzczogJHtwYXRofSAtPiBmaWxlSWQgJHtmaWxlSWR9YCk7XG4gICAgICByZXR1cm4gZmlsZUlkO1xuXG4gICAgfSBjYXRjaCAoZSkge1xuICAgICAgdGhpcy5lcnJvcigneE9wZW4gZXJyb3I6JywgZSk7XG4gICAgICByZXR1cm4gLTE7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIFJlYWQgZnJvbSBmaWxlXG4gICAqL1xuICB4UmVhZChmaWxlSWQsIGJ1ZmZlciwgYW1vdW50LCBvZmZzZXQpIHtcbiAgICB0cnkge1xuICAgICAgY29uc3QgZmlsZSA9IHRoaXMuI21hcEZpbGVJZFRvRmlsZS5nZXQoZmlsZUlkKTtcbiAgICAgIGlmICghZmlsZSkge1xuICAgICAgICB0aGlzLmVycm9yKGB4UmVhZDogaW52YWxpZCBmaWxlSWQgJHtmaWxlSWR9YCk7XG4gICAgICAgIHJldHVybiBTUUxJVEVfSU9FUlJfUkVBRDtcbiAgICAgIH1cblxuICAgICAgY29uc3QgblJlYWQgPSBmaWxlLnNhaC5yZWFkKGJ1ZmZlciwgeyBhdDogSEVBREVSX09GRlNFVF9EQVRBICsgb2Zmc2V0IH0pO1xuXG4gICAgICBpZiAoblJlYWQgPCBhbW91bnQpIHtcbiAgICAgICAgLy8gU2hvcnQgcmVhZCAtIGZpbGwgcmVzdCB3aXRoIHplcm9zXG4gICAgICAgIGJ1ZmZlci5maWxsKDAsIG5SZWFkKTtcbiAgICAgICAgcmV0dXJuIFNRTElURV9JT0VSUl9TSE9SVF9SRUFEO1xuICAgICAgfVxuXG4gICAgICByZXR1cm4gU1FMSVRFX09LO1xuXG4gICAgfSBjYXRjaCAoZSkge1xuICAgICAgdGhpcy5lcnJvcigneFJlYWQgZXJyb3I6JywgZSk7XG4gICAgICByZXR1cm4gU1FMSVRFX0lPRVJSX1JFQUQ7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIFdyaXRlIHRvIGZpbGVcbiAgICovXG4gIHhXcml0ZShmaWxlSWQsIGJ1ZmZlciwgYW1vdW50LCBvZmZzZXQpIHtcbiAgICB0cnkge1xuICAgICAgY29uc3QgZmlsZSA9IHRoaXMuI21hcEZpbGVJZFRvRmlsZS5nZXQoZmlsZUlkKTtcbiAgICAgIGlmICghZmlsZSkge1xuICAgICAgICB0aGlzLmVycm9yKGB4V3JpdGU6IGludmFsaWQgZmlsZUlkICR7ZmlsZUlkfWApO1xuICAgICAgICByZXR1cm4gU1FMSVRFX0lPRVJSX1dSSVRFO1xuICAgICAgfVxuXG4gICAgICBjb25zdCBuV3JpdHRlbiA9IGZpbGUuc2FoLndyaXRlKGJ1ZmZlciwgeyBhdDogSEVBREVSX09GRlNFVF9EQVRBICsgb2Zmc2V0IH0pO1xuXG4gICAgICBpZiAobldyaXR0ZW4gIT09IGFtb3VudCkge1xuICAgICAgICB0aGlzLmVycm9yKGB4V3JpdGU6IHdyb3RlICR7bldyaXR0ZW59LyR7YW1vdW50fSBieXRlc2ApO1xuICAgICAgICByZXR1cm4gU1FMSVRFX0lPRVJSX1dSSVRFO1xuICAgICAgfVxuXG4gICAgICByZXR1cm4gU1FMSVRFX09LO1xuXG4gICAgfSBjYXRjaCAoZSkge1xuICAgICAgdGhpcy5lcnJvcigneFdyaXRlIGVycm9yOicsIGUpO1xuICAgICAgcmV0dXJuIFNRTElURV9JT0VSUl9XUklURTtcbiAgICB9XG4gIH1cblxuICAvKipcbiAgICogU3luYyBmaWxlIHRvIHN0b3JhZ2VcbiAgICovXG4gIHhTeW5jKGZpbGVJZCwgZmxhZ3MpIHtcbiAgICB0cnkge1xuICAgICAgY29uc3QgZmlsZSA9IHRoaXMuI21hcEZpbGVJZFRvRmlsZS5nZXQoZmlsZUlkKTtcbiAgICAgIGlmICghZmlsZSkge1xuICAgICAgICByZXR1cm4gU1FMSVRFX0lPRVJSO1xuICAgICAgfVxuXG4gICAgICBmaWxlLnNhaC5mbHVzaCgpO1xuICAgICAgcmV0dXJuIFNRTElURV9PSztcblxuICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgIHRoaXMuZXJyb3IoJ3hTeW5jIGVycm9yOicsIGUpO1xuICAgICAgcmV0dXJuIFNRTElURV9JT0VSUjtcbiAgICB9XG4gIH1cblxuICAvKipcbiAgICogVHJ1bmNhdGUgZmlsZVxuICAgKi9cbiAgeFRydW5jYXRlKGZpbGVJZCwgc2l6ZSkge1xuICAgIHRyeSB7XG4gICAgICBjb25zdCBmaWxlID0gdGhpcy4jbWFwRmlsZUlkVG9GaWxlLmdldChmaWxlSWQpO1xuICAgICAgaWYgKCFmaWxlKSB7XG4gICAgICAgIHJldHVybiBTUUxJVEVfSU9FUlI7XG4gICAgICB9XG5cbiAgICAgIGZpbGUuc2FoLnRydW5jYXRlKEhFQURFUl9PRkZTRVRfREFUQSArIHNpemUpO1xuICAgICAgcmV0dXJuIFNRTElURV9PSztcblxuICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgIHRoaXMuZXJyb3IoJ3hUcnVuY2F0ZSBlcnJvcjonLCBlKTtcbiAgICAgIHJldHVybiBTUUxJVEVfSU9FUlI7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIEdldCBmaWxlIHNpemVcbiAgICovXG4gIHhGaWxlU2l6ZShmaWxlSWQpIHtcbiAgICB0cnkge1xuICAgICAgY29uc3QgZmlsZSA9IHRoaXMuI21hcEZpbGVJZFRvRmlsZS5nZXQoZmlsZUlkKTtcbiAgICAgIGlmICghZmlsZSkge1xuICAgICAgICByZXR1cm4gLTE7XG4gICAgICB9XG5cbiAgICAgIGNvbnN0IHNpemUgPSBmaWxlLnNhaC5nZXRTaXplKCkgLSBIRUFERVJfT0ZGU0VUX0RBVEE7XG4gICAgICByZXR1cm4gTWF0aC5tYXgoMCwgc2l6ZSk7XG5cbiAgICB9IGNhdGNoIChlKSB7XG4gICAgICB0aGlzLmVycm9yKCd4RmlsZVNpemUgZXJyb3I6JywgZSk7XG4gICAgICByZXR1cm4gLTE7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIENsb3NlIGZpbGVcbiAgICovXG4gIHhDbG9zZShmaWxlSWQpIHtcbiAgICB0cnkge1xuICAgICAgY29uc3QgZmlsZSA9IHRoaXMuI21hcEZpbGVJZFRvRmlsZS5nZXQoZmlsZUlkKTtcbiAgICAgIGlmICghZmlsZSkge1xuICAgICAgICByZXR1cm4gU1FMSVRFX0VSUk9SO1xuICAgICAgfVxuXG4gICAgICAvLyBEb24ndCBjbG9zZSB0aGUgU0FIIC0gaXQncyByZXVzZWQgaW4gdGhlIHBvb2xcbiAgICAgIC8vIEp1c3QgcmVtb3ZlIGZyb20gb3BlbiBmaWxlcyBtYXBcbiAgICAgIHRoaXMuI21hcEZpbGVJZFRvRmlsZS5kZWxldGUoZmlsZUlkKTtcblxuICAgICAgdGhpcy5sb2coYHhDbG9zZTogZmlsZUlkICR7ZmlsZUlkfSAoJHtmaWxlLnBhdGh9KWApO1xuICAgICAgcmV0dXJuIFNRTElURV9PSztcblxuICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgIHRoaXMuZXJyb3IoJ3hDbG9zZSBlcnJvcjonLCBlKTtcbiAgICAgIHJldHVybiBTUUxJVEVfRVJST1I7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIENoZWNrIGZpbGUgYWNjZXNzXG4gICAqL1xuICB4QWNjZXNzKGZpbGVuYW1lLCBmbGFncykge1xuICAgIHRyeSB7XG4gICAgICBjb25zdCBwYXRoID0gdGhpcy5nZXRQYXRoKGZpbGVuYW1lKTtcbiAgICAgIHJldHVybiB0aGlzLmhhc0ZpbGVuYW1lKHBhdGgpID8gMSA6IDA7XG4gICAgfSBjYXRjaCAoZSkge1xuICAgICAgdGhpcy5lcnJvcigneEFjY2VzcyBlcnJvcjonLCBlKTtcbiAgICAgIHJldHVybiAwO1xuICAgIH1cbiAgfVxuXG4gIC8qKlxuICAgKiBEZWxldGUgZmlsZVxuICAgKi9cbiAgeERlbGV0ZShmaWxlbmFtZSwgc3luY0Rpcikge1xuICAgIHRyeSB7XG4gICAgICBjb25zdCBwYXRoID0gdGhpcy5nZXRQYXRoKGZpbGVuYW1lKTtcbiAgICAgIHRoaXMubG9nKGB4RGVsZXRlOiAke3BhdGh9YCk7XG4gICAgICB0aGlzLmRlbGV0ZVBhdGgocGF0aCk7XG4gICAgICByZXR1cm4gU1FMSVRFX09LO1xuICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgIHRoaXMuZXJyb3IoJ3hEZWxldGUgZXJyb3I6JywgZSk7XG4gICAgICByZXR1cm4gU1FMSVRFX0lPRVJSO1xuICAgIH1cbiAgfVxufVxuXG4vLyBFeHBvcnQgc2luZ2xldG9uIGluc3RhbmNlXG5jb25zdCBvcGZzU0FIUG9vbCA9IG5ldyBPcGZzU0FIUG9vbCh7XG4gIGRpcmVjdG9yeTogJy5vcGZzLXNhaHBvb2wnLFxuICBpbml0aWFsQ2FwYWNpdHk6IDYsXG4gIGNsZWFyT25Jbml0OiBmYWxzZVxufSk7XG5cbi8vIE1ha2UgYXZhaWxhYmxlIGdsb2JhbGx5IGZvciBFTV9KUyBob29rc1xuaWYgKHR5cGVvZiBnbG9iYWxUaGlzICE9PSAndW5kZWZpbmVkJykge1xuICBnbG9iYWxUaGlzLm9wZnNTQUhQb29sID0gb3Bmc1NBSFBvb2w7XG59XG5cbi8vIEVTNiBtb2R1bGUgZXhwb3J0XG5leHBvcnQgeyBPcGZzU0FIUG9vbCwgb3Bmc1NBSFBvb2wgfTtcbiIsICIvLyBvcGZzLXdvcmtlci50c1xuLy8gV2ViIFdvcmtlciBmb3IgT1BGUyBmaWxlIEkvTyB1c2luZyBTQUhQb29sXG4vLyBIYW5kbGVzIG9ubHkgZmlsZSByZWFkL3dyaXRlIG9wZXJhdGlvbnMgLSBubyBTUUwgZXhlY3V0aW9uXG5cbmltcG9ydCB7IG9wZnNTQUhQb29sIH0gZnJvbSAnLi9vcGZzLXNhaHBvb2wnO1xuXG5pbnRlcmZhY2UgV29ya2VyTWVzc2FnZSB7XG4gICAgaWQ6IG51bWJlcjtcbiAgICB0eXBlOiBzdHJpbmc7XG4gICAgYXJncz86IGFueTtcbn1cblxuaW50ZXJmYWNlIFdvcmtlclJlc3BvbnNlIHtcbiAgICBpZDogbnVtYmVyO1xuICAgIHN1Y2Nlc3M6IGJvb2xlYW47XG4gICAgcmVzdWx0PzogYW55O1xuICAgIGVycm9yPzogc3RyaW5nO1xufVxuXG4vLyBXYWl0IGZvciBPUEZTIFNBSFBvb2wgdG8gaW5pdGlhbGl6ZVxub3Bmc1NBSFBvb2wuaXNSZWFkeS50aGVuKCgpID0+IHtcbiAgICBjb25zb2xlLmxvZygnW09QRlMgV29ya2VyXSBTQUhQb29sIGluaXRpYWxpemVkLCBzZW5kaW5nIHJlYWR5IHNpZ25hbCcpO1xuICAgIHNlbGYucG9zdE1lc3NhZ2UoeyB0eXBlOiAncmVhZHknIH0pO1xufSkuY2F0Y2goKGVycm9yKSA9PiB7XG4gICAgY29uc29sZS5lcnJvcignW09QRlMgV29ya2VyXSBTQUhQb29sIGluaXRpYWxpemF0aW9uIGZhaWxlZDonLCBlcnJvcik7XG4gICAgc2VsZi5wb3N0TWVzc2FnZSh7IHR5cGU6ICdlcnJvcicsIGVycm9yOiBlcnJvci5tZXNzYWdlIH0pO1xufSk7XG5cbi8vIEhhbmRsZSBtZXNzYWdlcyBmcm9tIG1haW4gdGhyZWFkXG5zZWxmLm9ubWVzc2FnZSA9IGFzeW5jIChldmVudDogTWVzc2FnZUV2ZW50PFdvcmtlck1lc3NhZ2U+KSA9PiB7XG4gICAgY29uc3QgeyBpZCwgdHlwZSwgYXJncyB9ID0gZXZlbnQuZGF0YTtcblxuICAgIHRyeSB7XG4gICAgICAgIGxldCByZXN1bHQ6IGFueTtcblxuICAgICAgICBzd2l0Y2ggKHR5cGUpIHtcbiAgICAgICAgICAgIGNhc2UgJ2NsZWFudXAnOlxuICAgICAgICAgICAgICAgIC8vIFJlbGVhc2UgYWxsIE9QRlMgaGFuZGxlcyBiZWZvcmUgcGFnZSB1bmxvYWRcbiAgICAgICAgICAgICAgICBjb25zb2xlLmxvZygnW09QRlMgV29ya2VyXSBDbGVhbmluZyB1cCBoYW5kbGVzIGJlZm9yZSB1bmxvYWQuLi4nKTtcbiAgICAgICAgICAgICAgICBvcGZzU0FIUG9vbC5yZWxlYXNlQWNjZXNzSGFuZGxlcygpO1xuICAgICAgICAgICAgICAgIGNvbnNvbGUubG9nKCdbT1BGUyBXb3JrZXJdIENsZWFudXAgY29tcGxldGUnKTtcbiAgICAgICAgICAgICAgICByZXN1bHQgPSB7IHN1Y2Nlc3M6IHRydWUgfTtcbiAgICAgICAgICAgICAgICBicmVhaztcblxuICAgICAgICAgICAgY2FzZSAnZ2V0Q2FwYWNpdHknOlxuICAgICAgICAgICAgICAgIHJlc3VsdCA9IHtcbiAgICAgICAgICAgICAgICAgICAgY2FwYWNpdHk6IG9wZnNTQUhQb29sLmdldENhcGFjaXR5KClcbiAgICAgICAgICAgICAgICB9O1xuICAgICAgICAgICAgICAgIGJyZWFrO1xuXG4gICAgICAgICAgICBjYXNlICdhZGRDYXBhY2l0eSc6XG4gICAgICAgICAgICAgICAgcmVzdWx0ID0ge1xuICAgICAgICAgICAgICAgICAgICBuZXdDYXBhY2l0eTogYXdhaXQgb3Bmc1NBSFBvb2wuYWRkQ2FwYWNpdHkoYXJncy5jb3VudClcbiAgICAgICAgICAgICAgICB9O1xuICAgICAgICAgICAgICAgIGJyZWFrO1xuXG4gICAgICAgICAgICBjYXNlICdnZXRGaWxlTGlzdCc6XG4gICAgICAgICAgICAgICAgcmVzdWx0ID0ge1xuICAgICAgICAgICAgICAgICAgICBmaWxlczogb3Bmc1NBSFBvb2wuZ2V0RmlsZU5hbWVzKClcbiAgICAgICAgICAgICAgICB9O1xuICAgICAgICAgICAgICAgIGJyZWFrO1xuXG4gICAgICAgICAgICBjYXNlICdyZWFkRmlsZSc6XG4gICAgICAgICAgICAgICAgLy8gUmVhZCBmaWxlIGZyb20gT1BGUyB1c2luZyBTQUhQb29sXG4gICAgICAgICAgICAgICAgY29uc3QgZmlsZUlkID0gb3Bmc1NBSFBvb2wueE9wZW4oYXJncy5maWxlbmFtZSwgMHgwMSk7IC8vIFJFQURPTkxZXG4gICAgICAgICAgICAgICAgaWYgKGZpbGVJZCA8IDApIHtcbiAgICAgICAgICAgICAgICAgICAgdGhyb3cgbmV3IEVycm9yKGBGaWxlIG5vdCBmb3VuZDogJHthcmdzLmZpbGVuYW1lfWApO1xuICAgICAgICAgICAgICAgIH1cblxuICAgICAgICAgICAgICAgIGNvbnN0IHNpemUgPSBvcGZzU0FIUG9vbC54RmlsZVNpemUoZmlsZUlkKTtcbiAgICAgICAgICAgICAgICBjb25zdCBidWZmZXIgPSBuZXcgVWludDhBcnJheShzaXplKTtcbiAgICAgICAgICAgICAgICBjb25zdCByZWFkUmVzdWx0ID0gb3Bmc1NBSFBvb2wueFJlYWQoZmlsZUlkLCBidWZmZXIsIHNpemUsIDApO1xuICAgICAgICAgICAgICAgIG9wZnNTQUhQb29sLnhDbG9zZShmaWxlSWQpO1xuXG4gICAgICAgICAgICAgICAgaWYgKHJlYWRSZXN1bHQgIT09IDApIHtcbiAgICAgICAgICAgICAgICAgICAgdGhyb3cgbmV3IEVycm9yKGBGYWlsZWQgdG8gcmVhZCBmaWxlOiAke2FyZ3MuZmlsZW5hbWV9YCk7XG4gICAgICAgICAgICAgICAgfVxuXG4gICAgICAgICAgICAgICAgcmVzdWx0ID0ge1xuICAgICAgICAgICAgICAgICAgICBkYXRhOiBBcnJheS5mcm9tKGJ1ZmZlcilcbiAgICAgICAgICAgICAgICB9O1xuICAgICAgICAgICAgICAgIGJyZWFrO1xuXG4gICAgICAgICAgICBjYXNlICd3cml0ZUZpbGUnOlxuICAgICAgICAgICAgICAgIC8vIFdyaXRlIGZpbGUgdG8gT1BGUyB1c2luZyBTQUhQb29sXG4gICAgICAgICAgICAgICAgY29uc3QgZGF0YSA9IG5ldyBVaW50OEFycmF5KGFyZ3MuZGF0YSk7XG4gICAgICAgICAgICAgICAgY29uc3Qgd3JpdGVGaWxlSWQgPSBvcGZzU0FIUG9vbC54T3BlbihcbiAgICAgICAgICAgICAgICAgICAgYXJncy5maWxlbmFtZSxcbiAgICAgICAgICAgICAgICAgICAgMHgwMiB8IDB4MDQgfCAweDEwMCAvLyBSRUFEV1JJVEUgfCBDUkVBVEUgfCBNQUlOX0RCXG4gICAgICAgICAgICAgICAgKTtcblxuICAgICAgICAgICAgICAgIGlmICh3cml0ZUZpbGVJZCA8IDApIHtcbiAgICAgICAgICAgICAgICAgICAgdGhyb3cgbmV3IEVycm9yKGBGYWlsZWQgdG8gb3BlbiBmaWxlIGZvciB3cml0aW5nOiAke2FyZ3MuZmlsZW5hbWV9YCk7XG4gICAgICAgICAgICAgICAgfVxuXG4gICAgICAgICAgICAgICAgLy8gVHJ1bmNhdGUgdG8gZXhhY3Qgc2l6ZVxuICAgICAgICAgICAgICAgIG9wZnNTQUhQb29sLnhUcnVuY2F0ZSh3cml0ZUZpbGVJZCwgZGF0YS5sZW5ndGgpO1xuXG4gICAgICAgICAgICAgICAgLy8gV3JpdGUgZGF0YVxuICAgICAgICAgICAgICAgIGNvbnN0IHdyaXRlUmVzdWx0ID0gb3Bmc1NBSFBvb2wueFdyaXRlKHdyaXRlRmlsZUlkLCBkYXRhLCBkYXRhLmxlbmd0aCwgMCk7XG5cbiAgICAgICAgICAgICAgICAvLyBTeW5jIHRvIGRpc2tcbiAgICAgICAgICAgICAgICBvcGZzU0FIUG9vbC54U3luYyh3cml0ZUZpbGVJZCwgMCk7XG4gICAgICAgICAgICAgICAgb3Bmc1NBSFBvb2wueENsb3NlKHdyaXRlRmlsZUlkKTtcblxuICAgICAgICAgICAgICAgIGlmICh3cml0ZVJlc3VsdCAhPT0gMCkge1xuICAgICAgICAgICAgICAgICAgICB0aHJvdyBuZXcgRXJyb3IoYEZhaWxlZCB0byB3cml0ZSBmaWxlOiAke2FyZ3MuZmlsZW5hbWV9YCk7XG4gICAgICAgICAgICAgICAgfVxuXG4gICAgICAgICAgICAgICAgcmVzdWx0ID0ge1xuICAgICAgICAgICAgICAgICAgICBieXRlc1dyaXR0ZW46IGRhdGEubGVuZ3RoXG4gICAgICAgICAgICAgICAgfTtcbiAgICAgICAgICAgICAgICBicmVhaztcblxuICAgICAgICAgICAgY2FzZSAnZGVsZXRlRmlsZSc6XG4gICAgICAgICAgICAgICAgY29uc3QgZGVsZXRlUmVzdWx0ID0gb3Bmc1NBSFBvb2wueERlbGV0ZShhcmdzLmZpbGVuYW1lLCAxKTtcbiAgICAgICAgICAgICAgICBpZiAoZGVsZXRlUmVzdWx0ICE9PSAwKSB7XG4gICAgICAgICAgICAgICAgICAgIHRocm93IG5ldyBFcnJvcihgRmFpbGVkIHRvIGRlbGV0ZSBmaWxlOiAke2FyZ3MuZmlsZW5hbWV9YCk7XG4gICAgICAgICAgICAgICAgfVxuICAgICAgICAgICAgICAgIHJlc3VsdCA9IHsgc3VjY2VzczogdHJ1ZSB9O1xuICAgICAgICAgICAgICAgIGJyZWFrO1xuXG4gICAgICAgICAgICBjYXNlICdmaWxlRXhpc3RzJzpcbiAgICAgICAgICAgICAgICBjb25zdCBleGlzdHMgPSBvcGZzU0FIUG9vbC54QWNjZXNzKGFyZ3MuZmlsZW5hbWUsIDApID09PSAwO1xuICAgICAgICAgICAgICAgIHJlc3VsdCA9IHsgZXhpc3RzIH07XG4gICAgICAgICAgICAgICAgYnJlYWs7XG5cbiAgICAgICAgICAgIGRlZmF1bHQ6XG4gICAgICAgICAgICAgICAgdGhyb3cgbmV3IEVycm9yKGBVbmtub3duIG1lc3NhZ2UgdHlwZTogJHt0eXBlfWApO1xuICAgICAgICB9XG5cbiAgICAgICAgY29uc3QgcmVzcG9uc2U6IFdvcmtlclJlc3BvbnNlID0ge1xuICAgICAgICAgICAgaWQsXG4gICAgICAgICAgICBzdWNjZXNzOiB0cnVlLFxuICAgICAgICAgICAgcmVzdWx0XG4gICAgICAgIH07XG4gICAgICAgIHNlbGYucG9zdE1lc3NhZ2UocmVzcG9uc2UpO1xuXG4gICAgfSBjYXRjaCAoZXJyb3IpIHtcbiAgICAgICAgY29uc3QgcmVzcG9uc2U6IFdvcmtlclJlc3BvbnNlID0ge1xuICAgICAgICAgICAgaWQsXG4gICAgICAgICAgICBzdWNjZXNzOiBmYWxzZSxcbiAgICAgICAgICAgIGVycm9yOiBlcnJvciBpbnN0YW5jZW9mIEVycm9yID8gZXJyb3IubWVzc2FnZSA6ICdVbmtub3duIGVycm9yJ1xuICAgICAgICB9O1xuICAgICAgICBzZWxmLnBvc3RNZXNzYWdlKHJlc3BvbnNlKTtcbiAgICB9XG59O1xuXG5jb25zb2xlLmxvZygnW09QRlMgV29ya2VyXSBXb3JrZXIgc2NyaXB0IGxvYWRlZCwgd2FpdGluZyBmb3IgU0FIUG9vbCBpbml0aWFsaXphdGlvbi4uLicpO1xuIl0sCiAgIm1hcHBp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
