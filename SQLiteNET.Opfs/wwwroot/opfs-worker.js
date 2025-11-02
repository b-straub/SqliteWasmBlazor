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
      case "persistDirtyPages":
        const { filename, pages } = args;
        if (!pages || pages.length === 0) {
          result = { pagesWritten: 0 };
          break;
        }
        console.log(`[OPFS Worker] Persisting ${pages.length} dirty pages for ${filename}`);
        const PAGE_SIZE = 4096;
        const SQLITE_OK2 = 0;
        const FLAGS_READWRITE = 2;
        const FLAGS_MAIN_DB = 256;
        const partialFileId = opfsSAHPool.xOpen(
          filename,
          FLAGS_READWRITE | FLAGS_MAIN_DB
        );
        if (partialFileId < 0) {
          throw new Error(`Failed to open file for partial write: ${filename}`);
        }
        let pagesWritten = 0;
        try {
          for (const page of pages) {
            const { pageNumber, data: data2 } = page;
            const offset = pageNumber * PAGE_SIZE;
            const pageBuffer = new Uint8Array(data2);
            const writeRc = opfsSAHPool.xWrite(
              partialFileId,
              pageBuffer,
              pageBuffer.length,
              offset
            );
            if (writeRc !== SQLITE_OK2) {
              throw new Error(`Failed to write page ${pageNumber} at offset ${offset}`);
            }
            pagesWritten++;
          }
          opfsSAHPool.xSync(partialFileId, 0);
          console.log(`[OPFS Worker] Successfully wrote ${pagesWritten} pages`);
        } finally {
          opfsSAHPool.xClose(partialFileId);
        }
        result = {
          pagesWritten,
          bytesWritten: pagesWritten * PAGE_SIZE
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
//# sourceMappingURL=data:application/json;base64,ewogICJ2ZXJzaW9uIjogMywKICAic291cmNlcyI6IFsiLi4vVHlwZXNjcmlwdC9vcGZzLXNhaHBvb2wudHMiLCAiLi4vVHlwZXNjcmlwdC9vcGZzLXdvcmtlci50cyJdLAogICJzb3VyY2VzQ29udGVudCI6IFsiLyoqXG4gKiBvcGZzLXNhaHBvb2wuanMgLSBTdGFuZGFsb25lIE9wZnNTQUhQb29sIFZGUyBJbXBsZW1lbnRhdGlvblxuICpcbiAqIEJhc2VkIG9uIEBzcWxpdGUub3JnL3NxbGl0ZS13YXNtJ3MgT3Bmc1NBSFBvb2wgYnkgdGhlIFNRTGl0ZSB0ZWFtXG4gKiBXaGljaCBpcyBiYXNlZCBvbiBSb3kgSGFzaGltb3RvJ3MgQWNjZXNzSGFuZGxlUG9vbFZGU1xuICpcbiAqIFRoaXMgaXMgYSBzaW1wbGlmaWVkIHZlcnNpb24gZm9yIGRpcmVjdCBpbnRlZ3JhdGlvbiB3aXRoIG91ciBjdXN0b20gZV9zcWxpdGUzX2pzdmZzLmFcbiAqIE5vIHdvcmtlciBtZXNzYWdpbmcgLSBydW5zIGRpcmVjdGx5IGluIHRoZSB3b3JrZXIgY29udGV4dCB3aXRoIG91ciBXQVNNIG1vZHVsZS5cbiAqL1xuXG4vLyBDb25zdGFudHMgbWF0Y2hpbmcgU1FMaXRlIFZGUyByZXF1aXJlbWVudHNcbmNvbnN0IFNFQ1RPUl9TSVpFID0gNDA5NjtcbmNvbnN0IEhFQURFUl9NQVhfUEFUSF9TSVpFID0gNTEyO1xuY29uc3QgSEVBREVSX0ZMQUdTX1NJWkUgPSA0O1xuY29uc3QgSEVBREVSX0RJR0VTVF9TSVpFID0gODtcbmNvbnN0IEhFQURFUl9DT1JQVVNfU0laRSA9IEhFQURFUl9NQVhfUEFUSF9TSVpFICsgSEVBREVSX0ZMQUdTX1NJWkU7XG5jb25zdCBIRUFERVJfT0ZGU0VUX0ZMQUdTID0gSEVBREVSX01BWF9QQVRIX1NJWkU7XG5jb25zdCBIRUFERVJfT0ZGU0VUX0RJR0VTVCA9IEhFQURFUl9DT1JQVVNfU0laRTtcbmNvbnN0IEhFQURFUl9PRkZTRVRfREFUQSA9IFNFQ1RPUl9TSVpFO1xuXG4vLyBTUUxpdGUgZmlsZSB0eXBlIGZsYWdzXG5jb25zdCBTUUxJVEVfT1BFTl9NQUlOX0RCID0gMHgwMDAwMDEwMDtcbmNvbnN0IFNRTElURV9PUEVOX01BSU5fSk9VUk5BTCA9IDB4MDAwMDA4MDA7XG5jb25zdCBTUUxJVEVfT1BFTl9TVVBFUl9KT1VSTkFMID0gMHgwMDAwNDAwMDtcbmNvbnN0IFNRTElURV9PUEVOX1dBTCA9IDB4MDAwODAwMDA7XG5jb25zdCBTUUxJVEVfT1BFTl9DUkVBVEUgPSAweDAwMDAwMDA0O1xuY29uc3QgU1FMSVRFX09QRU5fREVMRVRFT05DTE9TRSA9IDB4MDAwMDAwMDg7XG5jb25zdCBTUUxJVEVfT1BFTl9NRU1PUlkgPSAweDAwMDAwMDgwOyAvLyBVc2VkIGFzIEZMQUdfQ09NUFVURV9ESUdFU1RfVjJcblxuY29uc3QgUEVSU0lTVEVOVF9GSUxFX1RZUEVTID1cbiAgU1FMSVRFX09QRU5fTUFJTl9EQiB8XG4gIFNRTElURV9PUEVOX01BSU5fSk9VUk5BTCB8XG4gIFNRTElURV9PUEVOX1NVUEVSX0pPVVJOQUwgfFxuICBTUUxJVEVfT1BFTl9XQUw7XG5cbmNvbnN0IEZMQUdfQ09NUFVURV9ESUdFU1RfVjIgPSBTUUxJVEVfT1BFTl9NRU1PUlk7XG5jb25zdCBPUEFRVUVfRElSX05BTUUgPSAnLm9wYXF1ZSc7XG5cbi8vIFNRTGl0ZSByZXN1bHQgY29kZXNcbmNvbnN0IFNRTElURV9PSyA9IDA7XG5jb25zdCBTUUxJVEVfRVJST1IgPSAxO1xuY29uc3QgU1FMSVRFX0lPRVJSID0gMTA7XG5jb25zdCBTUUxJVEVfSU9FUlJfU0hPUlRfUkVBRCA9IDUyMjsgLy8gU1FMSVRFX0lPRVJSIHwgKDI8PDgpXG5jb25zdCBTUUxJVEVfSU9FUlJfV1JJVEUgPSA3Nzg7IC8vIFNRTElURV9JT0VSUiB8ICgzPDw4KVxuY29uc3QgU1FMSVRFX0lPRVJSX1JFQUQgPSAyNjY7IC8vIFNRTElURV9JT0VSUiB8ICgxPDw4KVxuY29uc3QgU1FMSVRFX0NBTlRPUEVOID0gMTQ7XG5jb25zdCBTUUxJVEVfTE9DS19OT05FID0gMDtcblxuY29uc3QgZ2V0UmFuZG9tTmFtZSA9ICgpID0+IE1hdGgucmFuZG9tKCkudG9TdHJpbmcoMzYpLnNsaWNlKDIpO1xuY29uc3QgdGV4dERlY29kZXIgPSBuZXcgVGV4dERlY29kZXIoKTtcbmNvbnN0IHRleHRFbmNvZGVyID0gbmV3IFRleHRFbmNvZGVyKCk7XG5cbi8qKlxuICogT3Bmc1NBSFBvb2wgLSBQb29sLWJhc2VkIE9QRlMgVkZTIHdpdGggU3luY2hyb25vdXMgQWNjZXNzIEhhbmRsZXNcbiAqXG4gKiBNYW5hZ2VzIGEgcG9vbCBvZiBwcmUtYWxsb2NhdGVkIE9QRlMgZmlsZXMgd2l0aCBzeW5jaHJvbm91cyBhY2Nlc3MgaGFuZGxlcy5cbiAqIEZpbGVzIGFyZSBzdG9yZWQgd2l0aCBhIDQwOTYtYnl0ZSBoZWFkZXIgY29udGFpbmluZyBtZXRhZGF0YS5cbiAqL1xuY2xhc3MgT3Bmc1NBSFBvb2wge1xuICAjZGhWZnNSb290ID0gbnVsbDtcbiAgI2RoT3BhcXVlID0gbnVsbDtcbiAgI2RoVmZzUGFyZW50ID0gbnVsbDtcblxuICAvLyBQb29sIG1hbmFnZW1lbnRcbiAgI21hcFNBSFRvTmFtZSA9IG5ldyBNYXAoKTsgICAgICAgLy8gU0FIIC0+IHJhbmRvbSBPUEZTIGZpbGVuYW1lXG4gICNtYXBGaWxlbmFtZVRvU0FIID0gbmV3IE1hcCgpOyAgIC8vIFNRTGl0ZSBwYXRoIC0+IFNBSFxuICAjYXZhaWxhYmxlU0FIID0gbmV3IFNldCgpOyAgICAgICAvLyBVbmFzc29jaWF0ZWQgU0FIcyByZWFkeSBmb3IgdXNlXG5cbiAgLy8gRmlsZSBoYW5kbGUgdHJhY2tpbmcgZm9yIG9wZW4gZmlsZXNcbiAgI21hcEZpbGVJZFRvRmlsZSA9IG5ldyBNYXAoKTsgICAgLy8gZmlsZUlkIC0+IHtwYXRoLCBzYWgsIGxvY2tUeXBlLCBmbGFnc31cbiAgI25leHRGaWxlSWQgPSAxO1xuXG4gIC8vIEhlYWRlciBidWZmZXIgZm9yIHJlYWRpbmcvd3JpdGluZyBmaWxlIG1ldGFkYXRhXG4gICNhcEJvZHkgPSBuZXcgVWludDhBcnJheShIRUFERVJfQ09SUFVTX1NJWkUpO1xuICAjZHZCb2R5ID0gbnVsbDtcblxuICB2ZnNEaXIgPSBudWxsO1xuXG4gIGNvbnN0cnVjdG9yKG9wdGlvbnMgPSB7fSkge1xuICAgIHRoaXMudmZzRGlyID0gb3B0aW9ucy5kaXJlY3RvcnkgfHwgJy5vcGZzLXNhaHBvb2wnO1xuICAgIHRoaXMuI2R2Qm9keSA9IG5ldyBEYXRhVmlldyh0aGlzLiNhcEJvZHkuYnVmZmVyLCB0aGlzLiNhcEJvZHkuYnl0ZU9mZnNldCk7XG4gICAgdGhpcy5pc1JlYWR5ID0gdGhpcy5yZXNldChvcHRpb25zLmNsZWFyT25Jbml0IHx8IGZhbHNlKVxuICAgICAgLnRoZW4oKCkgPT4ge1xuICAgICAgICBjb25zdCBjYXBhY2l0eSA9IHRoaXMuZ2V0Q2FwYWNpdHkoKTtcbiAgICAgICAgaWYgKGNhcGFjaXR5ID4gMCkge1xuICAgICAgICAgIHJldHVybiBQcm9taXNlLnJlc29sdmUoKTtcbiAgICAgICAgfVxuICAgICAgICByZXR1cm4gdGhpcy5hZGRDYXBhY2l0eShvcHRpb25zLmluaXRpYWxDYXBhY2l0eSB8fCA2KTtcbiAgICAgIH0pO1xuICB9XG5cbiAgbG9nKC4uLmFyZ3MpIHtcbiAgICBjb25zb2xlLmxvZygnW09wZnNTQUhQb29sXScsIC4uLmFyZ3MpO1xuICB9XG5cbiAgd2FybiguLi5hcmdzKSB7XG4gICAgY29uc29sZS53YXJuKCdbT3Bmc1NBSFBvb2xdJywgLi4uYXJncyk7XG4gIH1cblxuICBlcnJvciguLi5hcmdzKSB7XG4gICAgY29uc29sZS5lcnJvcignW09wZnNTQUhQb29sXScsIC4uLmFyZ3MpO1xuICB9XG5cbiAgZ2V0Q2FwYWNpdHkoKSB7XG4gICAgcmV0dXJuIHRoaXMuI21hcFNBSFRvTmFtZS5zaXplO1xuICB9XG5cbiAgZ2V0RmlsZUNvdW50KCkge1xuICAgIHJldHVybiB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILnNpemU7XG4gIH1cblxuICBnZXRGaWxlTmFtZXMoKSB7XG4gICAgcmV0dXJuIEFycmF5LmZyb20odGhpcy4jbWFwRmlsZW5hbWVUb1NBSC5rZXlzKCkpO1xuICB9XG5cbiAgLyoqXG4gICAqIEFkZCBjYXBhY2l0eSAtIGNyZWF0ZSBuIG5ldyBPUEZTIGZpbGVzIHdpdGggc3luYyBhY2Nlc3MgaGFuZGxlc1xuICAgKi9cbiAgYXN5bmMgYWRkQ2FwYWNpdHkobikge1xuICAgIGZvciAobGV0IGkgPSAwOyBpIDwgbjsgKytpKSB7XG4gICAgICBjb25zdCBuYW1lID0gZ2V0UmFuZG9tTmFtZSgpO1xuICAgICAgY29uc3QgaCA9IGF3YWl0IHRoaXMuI2RoT3BhcXVlLmdldEZpbGVIYW5kbGUobmFtZSwgeyBjcmVhdGU6IHRydWUgfSk7XG4gICAgICBjb25zdCBhaCA9IGF3YWl0IGguY3JlYXRlU3luY0FjY2Vzc0hhbmRsZSgpO1xuICAgICAgdGhpcy4jbWFwU0FIVG9OYW1lLnNldChhaCwgbmFtZSk7XG4gICAgICB0aGlzLnNldEFzc29jaWF0ZWRQYXRoKGFoLCAnJywgMCk7XG4gICAgfVxuICAgIHRoaXMubG9nKGBBZGRlZCAke259IGhhbmRsZXMsIHRvdGFsIGNhcGFjaXR5OiAke3RoaXMuZ2V0Q2FwYWNpdHkoKX1gKTtcbiAgICByZXR1cm4gdGhpcy5nZXRDYXBhY2l0eSgpO1xuICB9XG5cbiAgLyoqXG4gICAqIFJlbGVhc2UgYWxsIGFjY2VzcyBoYW5kbGVzIChjbGVhbnVwKVxuICAgKi9cbiAgcmVsZWFzZUFjY2Vzc0hhbmRsZXMoKSB7XG4gICAgZm9yIChjb25zdCBhaCBvZiB0aGlzLiNtYXBTQUhUb05hbWUua2V5cygpKSB7XG4gICAgICB0cnkge1xuICAgICAgICBhaC5jbG9zZSgpO1xuICAgICAgfSBjYXRjaCAoZSkge1xuICAgICAgICB0aGlzLndhcm4oJ0Vycm9yIGNsb3NpbmcgaGFuZGxlOicsIGUpO1xuICAgICAgfVxuICAgIH1cbiAgICB0aGlzLiNtYXBTQUhUb05hbWUuY2xlYXIoKTtcbiAgICB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILmNsZWFyKCk7XG4gICAgdGhpcy4jYXZhaWxhYmxlU0FILmNsZWFyKCk7XG4gICAgdGhpcy4jbWFwRmlsZUlkVG9GaWxlLmNsZWFyKCk7XG4gIH1cblxuICAvKipcbiAgICogQWNxdWlyZSBhbGwgZXhpc3RpbmcgYWNjZXNzIGhhbmRsZXMgZnJvbSBPUEZTIGRpcmVjdG9yeSB3aXRoIHJldHJ5IGxvZ2ljXG4gICAqL1xuICBhc3luYyBhY3F1aXJlQWNjZXNzSGFuZGxlcyhjbGVhckZpbGVzID0gZmFsc2UpIHtcbiAgICBjb25zdCBmaWxlcyA9IFtdO1xuICAgIGZvciBhd2FpdCAoY29uc3QgW25hbWUsIGhdIG9mIHRoaXMuI2RoT3BhcXVlKSB7XG4gICAgICBpZiAoJ2ZpbGUnID09PSBoLmtpbmQpIHtcbiAgICAgICAgZmlsZXMucHVzaChbbmFtZSwgaF0pO1xuICAgICAgfVxuICAgIH1cblxuICAgIC8vIFRyeSB0byBhY3F1aXJlIGhhbmRsZXMgd2l0aCByZXRyaWVzIHRvIGFsbG93IEdDIHRvIHJlbGVhc2Ugb2xkIGhhbmRsZXNcbiAgICBjb25zdCBtYXhSZXRyaWVzID0gMztcbiAgICBjb25zdCByZXRyeURlbGF5ID0gMTAwOyAvLyBtc1xuXG4gICAgZm9yIChsZXQgYXR0ZW1wdCA9IDA7IGF0dGVtcHQgPCBtYXhSZXRyaWVzOyBhdHRlbXB0KyspIHtcbiAgICAgIGlmIChhdHRlbXB0ID4gMCkge1xuICAgICAgICB0aGlzLndhcm4oYFJldHJ5ICR7YXR0ZW1wdH0vJHttYXhSZXRyaWVzIC0gMX0gYWZ0ZXIgJHtyZXRyeURlbGF5fW1zIGRlbGF5Li4uYCk7XG4gICAgICAgIGF3YWl0IG5ldyBQcm9taXNlKHJlc29sdmUgPT4gc2V0VGltZW91dChyZXNvbHZlLCByZXRyeURlbGF5ICogYXR0ZW1wdCkpO1xuICAgICAgfVxuXG4gICAgICBjb25zdCByZXN1bHRzID0gYXdhaXQgUHJvbWlzZS5hbGxTZXR0bGVkKFxuICAgICAgICBmaWxlcy5tYXAoYXN5bmMgKFtuYW1lLCBoXSkgPT4ge1xuICAgICAgICAgIHRyeSB7XG4gICAgICAgICAgICBjb25zdCBhaCA9IGF3YWl0IGguY3JlYXRlU3luY0FjY2Vzc0hhbmRsZSgpO1xuICAgICAgICAgICAgdGhpcy4jbWFwU0FIVG9OYW1lLnNldChhaCwgbmFtZSk7XG5cbiAgICAgICAgICAgIGlmIChjbGVhckZpbGVzKSB7XG4gICAgICAgICAgICAgIGFoLnRydW5jYXRlKEhFQURFUl9PRkZTRVRfREFUQSk7XG4gICAgICAgICAgICAgIHRoaXMuc2V0QXNzb2NpYXRlZFBhdGgoYWgsICcnLCAwKTtcbiAgICAgICAgICAgIH0gZWxzZSB7XG4gICAgICAgICAgICAgIGNvbnN0IHBhdGggPSB0aGlzLmdldEFzc29jaWF0ZWRQYXRoKGFoKTtcbiAgICAgICAgICAgICAgaWYgKHBhdGgpIHtcbiAgICAgICAgICAgICAgICB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILnNldChwYXRoLCBhaCk7XG4gICAgICAgICAgICAgICAgdGhpcy5sb2coYFJlc3RvcmVkIGZpbGUgYXNzb2NpYXRpb246ICR7cGF0aH0gLT4gJHtuYW1lfWApO1xuICAgICAgICAgICAgICB9IGVsc2Uge1xuICAgICAgICAgICAgICAgIHRoaXMuI2F2YWlsYWJsZVNBSC5hZGQoYWgpO1xuICAgICAgICAgICAgICB9XG4gICAgICAgICAgICB9XG4gICAgICAgICAgfSBjYXRjaCAoZSkge1xuICAgICAgICAgICAgaWYgKGUubmFtZSA9PT0gJ05vTW9kaWZpY2F0aW9uQWxsb3dlZEVycm9yJykge1xuICAgICAgICAgICAgICAvLyBGaWxlIGlzIGxvY2tlZCAtIHdpbGwgcmV0cnkgb3IgZGVsZXRlIG9uIGxhc3QgYXR0ZW1wdFxuICAgICAgICAgICAgICB0aHJvdyBlO1xuICAgICAgICAgICAgfSBlbHNlIHtcbiAgICAgICAgICAgICAgdGhpcy5lcnJvcignRXJyb3IgYWNxdWlyaW5nIGhhbmRsZTonLCBlKTtcbiAgICAgICAgICAgICAgdGhpcy5yZWxlYXNlQWNjZXNzSGFuZGxlcygpO1xuICAgICAgICAgICAgICB0aHJvdyBlO1xuICAgICAgICAgICAgfVxuICAgICAgICAgIH1cbiAgICAgICAgfSlcbiAgICAgICk7XG5cbiAgICAgIGNvbnN0IGxvY2tlZCA9IHJlc3VsdHMuZmlsdGVyKHIgPT5cbiAgICAgICAgci5zdGF0dXMgPT09ICdyZWplY3RlZCcgJiZcbiAgICAgICAgci5yZWFzb24/Lm5hbWUgPT09ICdOb01vZGlmaWNhdGlvbkFsbG93ZWRFcnJvcidcbiAgICAgICk7XG5cbiAgICAgIC8vIElmIHdlIGFjcXVpcmVkIHNvbWUgaGFuZGxlcyBvciB0aGlzIGlzIHRoZSBsYXN0IGF0dGVtcHQsIGRlY2lkZSB3aGF0IHRvIGRvXG4gICAgICBpZiAobG9ja2VkLmxlbmd0aCA9PT0gMCB8fCBhdHRlbXB0ID09PSBtYXhSZXRyaWVzIC0gMSkge1xuICAgICAgICBpZiAobG9ja2VkLmxlbmd0aCA+IDApIHtcbiAgICAgICAgICAvLyBMYXN0IGF0dGVtcHQgLSBkZWxldGUgbG9ja2VkIGZpbGVzIGFzIGxhc3QgcmVzb3J0XG4gICAgICAgICAgdGhpcy53YXJuKGAke2xvY2tlZC5sZW5ndGh9IGZpbGVzIHN0aWxsIGxvY2tlZCBhZnRlciAke21heFJldHJpZXN9IGF0dGVtcHRzLCBkZWxldGluZy4uLmApO1xuICAgICAgICAgIGZvciAobGV0IGkgPSAwOyBpIDwgZmlsZXMubGVuZ3RoOyBpKyspIHtcbiAgICAgICAgICAgIGlmIChyZXN1bHRzW2ldLnN0YXR1cyA9PT0gJ3JlamVjdGVkJyAmJiByZXN1bHRzW2ldLnJlYXNvbj8ubmFtZSA9PT0gJ05vTW9kaWZpY2F0aW9uQWxsb3dlZEVycm9yJykge1xuICAgICAgICAgICAgICBjb25zdCBbbmFtZV0gPSBmaWxlc1tpXTtcbiAgICAgICAgICAgICAgdHJ5IHtcbiAgICAgICAgICAgICAgICBhd2FpdCB0aGlzLiNkaE9wYXF1ZS5yZW1vdmVFbnRyeShuYW1lKTtcbiAgICAgICAgICAgICAgICB0aGlzLmxvZyhgRGVsZXRlZCBsb2NrZWQgZmlsZTogJHtuYW1lfWApO1xuICAgICAgICAgICAgICB9IGNhdGNoIChkZWxldGVFcnJvcikge1xuICAgICAgICAgICAgICAgIHRoaXMud2FybihgQ291bGQgbm90IGRlbGV0ZSBsb2NrZWQgZmlsZTogJHtuYW1lfWAsIGRlbGV0ZUVycm9yKTtcbiAgICAgICAgICAgICAgfVxuICAgICAgICAgICAgfVxuICAgICAgICAgIH1cbiAgICAgICAgfVxuXG4gICAgICAgIC8vIENoZWNrIGlmIHdlIGhhdmUgYW55IGNhcGFjaXR5IGFmdGVyIGFsbCBhdHRlbXB0c1xuICAgICAgICBpZiAodGhpcy5nZXRDYXBhY2l0eSgpID09PSAwICYmIGZpbGVzLmxlbmd0aCA+IDApIHtcbiAgICAgICAgICB0aHJvdyBuZXcgRXJyb3IoYEZhaWxlZCB0byBhY3F1aXJlIGFueSBhY2Nlc3MgaGFuZGxlcyBmcm9tICR7ZmlsZXMubGVuZ3RofSBmaWxlc2ApO1xuICAgICAgICB9XG5cbiAgICAgICAgYnJlYWs7IC8vIEV4aXQgcmV0cnkgbG9vcFxuICAgICAgfVxuXG4gICAgICAvLyBDbGVhciBtYXBzIGZvciBuZXh0IHJldHJ5XG4gICAgICB0aGlzLiNtYXBTQUhUb05hbWUuY2xlYXIoKTtcbiAgICAgIHRoaXMuI21hcEZpbGVuYW1lVG9TQUguY2xlYXIoKTtcbiAgICAgIHRoaXMuI2F2YWlsYWJsZVNBSC5jbGVhcigpO1xuICAgIH1cbiAgfVxuXG4gIC8qKlxuICAgKiBHZXQgYXNzb2NpYXRlZCBwYXRoIGZyb20gU0FIIGhlYWRlclxuICAgKi9cbiAgZ2V0QXNzb2NpYXRlZFBhdGgoc2FoKSB7XG4gICAgc2FoLnJlYWQodGhpcy4jYXBCb2R5LCB7IGF0OiAwIH0pO1xuXG4gICAgY29uc3QgZmxhZ3MgPSB0aGlzLiNkdkJvZHkuZ2V0VWludDMyKEhFQURFUl9PRkZTRVRfRkxBR1MpO1xuXG4gICAgLy8gQ2hlY2sgaWYgZmlsZSBzaG91bGQgYmUgZGVsZXRlZFxuICAgIGlmIChcbiAgICAgIHRoaXMuI2FwQm9keVswXSAmJlxuICAgICAgKGZsYWdzICYgU1FMSVRFX09QRU5fREVMRVRFT05DTE9TRSB8fFxuICAgICAgICAoZmxhZ3MgJiBQRVJTSVNURU5UX0ZJTEVfVFlQRVMpID09PSAwKVxuICAgICkge1xuICAgICAgdGhpcy53YXJuKGBSZW1vdmluZyBmaWxlIHdpdGggdW5leHBlY3RlZCBmbGFncyAke2ZsYWdzLnRvU3RyaW5nKDE2KX1gKTtcbiAgICAgIHRoaXMuc2V0QXNzb2NpYXRlZFBhdGgoc2FoLCAnJywgMCk7XG4gICAgICByZXR1cm4gJyc7XG4gICAgfVxuXG4gICAgLy8gVmVyaWZ5IGRpZ2VzdFxuICAgIGNvbnN0IGZpbGVEaWdlc3QgPSBuZXcgVWludDMyQXJyYXkoSEVBREVSX0RJR0VTVF9TSVpFIC8gNCk7XG4gICAgc2FoLnJlYWQoZmlsZURpZ2VzdCwgeyBhdDogSEVBREVSX09GRlNFVF9ESUdFU1QgfSk7XG4gICAgY29uc3QgY29tcERpZ2VzdCA9IHRoaXMuY29tcHV0ZURpZ2VzdCh0aGlzLiNhcEJvZHksIGZsYWdzKTtcblxuICAgIGlmIChmaWxlRGlnZXN0LmV2ZXJ5KCh2LCBpKSA9PiB2ID09PSBjb21wRGlnZXN0W2ldKSkge1xuICAgICAgY29uc3QgcGF0aEJ5dGVzID0gdGhpcy4jYXBCb2R5LmZpbmRJbmRleCgodikgPT4gMCA9PT0gdik7XG4gICAgICBpZiAoMCA9PT0gcGF0aEJ5dGVzKSB7XG4gICAgICAgIHNhaC50cnVuY2F0ZShIRUFERVJfT0ZGU0VUX0RBVEEpO1xuICAgICAgICByZXR1cm4gJyc7XG4gICAgICB9XG4gICAgICByZXR1cm4gdGV4dERlY29kZXIuZGVjb2RlKHRoaXMuI2FwQm9keS5zdWJhcnJheSgwLCBwYXRoQnl0ZXMpKTtcbiAgICB9IGVsc2Uge1xuICAgICAgdGhpcy53YXJuKCdEaXNhc3NvY2lhdGluZyBmaWxlIHdpdGggYmFkIGRpZ2VzdCcpO1xuICAgICAgdGhpcy5zZXRBc3NvY2lhdGVkUGF0aChzYWgsICcnLCAwKTtcbiAgICAgIHJldHVybiAnJztcbiAgICB9XG4gIH1cblxuICAvKipcbiAgICogU2V0IGFzc29jaWF0ZWQgcGF0aCBpbiBTQUggaGVhZGVyXG4gICAqL1xuICBzZXRBc3NvY2lhdGVkUGF0aChzYWgsIHBhdGgsIGZsYWdzKSB7XG4gICAgY29uc3QgZW5jID0gdGV4dEVuY29kZXIuZW5jb2RlSW50byhwYXRoLCB0aGlzLiNhcEJvZHkpO1xuICAgIGlmIChIRUFERVJfTUFYX1BBVEhfU0laRSA8PSBlbmMud3JpdHRlbiArIDEpIHtcbiAgICAgIHRocm93IG5ldyBFcnJvcihgUGF0aCB0b28gbG9uZzogJHtwYXRofWApO1xuICAgIH1cblxuICAgIGlmIChwYXRoICYmIGZsYWdzKSB7XG4gICAgICBmbGFncyB8PSBGTEFHX0NPTVBVVEVfRElHRVNUX1YyO1xuICAgIH1cblxuICAgIHRoaXMuI2FwQm9keS5maWxsKDAsIGVuYy53cml0dGVuLCBIRUFERVJfTUFYX1BBVEhfU0laRSk7XG4gICAgdGhpcy4jZHZCb2R5LnNldFVpbnQzMihIRUFERVJfT0ZGU0VUX0ZMQUdTLCBmbGFncyk7XG4gICAgY29uc3QgZGlnZXN0ID0gdGhpcy5jb21wdXRlRGlnZXN0KHRoaXMuI2FwQm9keSwgZmxhZ3MpO1xuXG4gICAgc2FoLndyaXRlKHRoaXMuI2FwQm9keSwgeyBhdDogMCB9KTtcbiAgICBzYWgud3JpdGUoZGlnZXN0LCB7IGF0OiBIRUFERVJfT0ZGU0VUX0RJR0VTVCB9KTtcbiAgICBzYWguZmx1c2goKTtcblxuICAgIGlmIChwYXRoKSB7XG4gICAgICB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILnNldChwYXRoLCBzYWgpO1xuICAgICAgdGhpcy4jYXZhaWxhYmxlU0FILmRlbGV0ZShzYWgpO1xuICAgIH0gZWxzZSB7XG4gICAgICBzYWgudHJ1bmNhdGUoSEVBREVSX09GRlNFVF9EQVRBKTtcbiAgICAgIHRoaXMuI2F2YWlsYWJsZVNBSC5hZGQoc2FoKTtcbiAgICB9XG4gIH1cblxuICAvKipcbiAgICogQ29tcHV0ZSBkaWdlc3QgZm9yIGZpbGUgaGVhZGVyIChjeXJiNTMtaW5zcGlyZWQgaGFzaClcbiAgICovXG4gIGNvbXB1dGVEaWdlc3QoYnl0ZUFycmF5LCBmaWxlRmxhZ3MpIHtcbiAgICBpZiAoZmlsZUZsYWdzICYgRkxBR19DT01QVVRFX0RJR0VTVF9WMikge1xuICAgICAgbGV0IGgxID0gMHhkZWFkYmVlZjtcbiAgICAgIGxldCBoMiA9IDB4NDFjNmNlNTc7XG4gICAgICBmb3IgKGNvbnN0IHYgb2YgYnl0ZUFycmF5KSB7XG4gICAgICAgIGgxID0gTWF0aC5pbXVsKGgxIF4gdiwgMjY1NDQzNTc2MSk7XG4gICAgICAgIGgyID0gTWF0aC5pbXVsKGgyIF4gdiwgMTA0NzI5KTtcbiAgICAgIH1cbiAgICAgIHJldHVybiBuZXcgVWludDMyQXJyYXkoW2gxID4+PiAwLCBoMiA+Pj4gMF0pO1xuICAgIH0gZWxzZSB7XG4gICAgICByZXR1cm4gbmV3IFVpbnQzMkFycmF5KFswLCAwXSk7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIFJlc2V0L2luaXRpYWxpemUgdGhlIHBvb2xcbiAgICovXG4gIGFzeW5jIHJlc2V0KGNsZWFyRmlsZXMpIHtcbiAgICBsZXQgaCA9IGF3YWl0IG5hdmlnYXRvci5zdG9yYWdlLmdldERpcmVjdG9yeSgpO1xuICAgIGxldCBwcmV2O1xuXG4gICAgZm9yIChjb25zdCBkIG9mIHRoaXMudmZzRGlyLnNwbGl0KCcvJykpIHtcbiAgICAgIGlmIChkKSB7XG4gICAgICAgIHByZXYgPSBoO1xuICAgICAgICBoID0gYXdhaXQgaC5nZXREaXJlY3RvcnlIYW5kbGUoZCwgeyBjcmVhdGU6IHRydWUgfSk7XG4gICAgICB9XG4gICAgfVxuXG4gICAgdGhpcy4jZGhWZnNSb290ID0gaDtcbiAgICB0aGlzLiNkaFZmc1BhcmVudCA9IHByZXY7XG4gICAgdGhpcy4jZGhPcGFxdWUgPSBhd2FpdCB0aGlzLiNkaFZmc1Jvb3QuZ2V0RGlyZWN0b3J5SGFuZGxlKE9QQVFVRV9ESVJfTkFNRSwge1xuICAgICAgY3JlYXRlOiB0cnVlLFxuICAgIH0pO1xuXG4gICAgdGhpcy5yZWxlYXNlQWNjZXNzSGFuZGxlcygpO1xuICAgIHJldHVybiB0aGlzLmFjcXVpcmVBY2Nlc3NIYW5kbGVzKGNsZWFyRmlsZXMpO1xuICB9XG5cbiAgLyoqXG4gICAqIEdldCBwYXRoIChoYW5kbGUgYm90aCBzdHJpbmcgYW5kIHBvaW50ZXIpXG4gICAqL1xuICBnZXRQYXRoKGFyZykge1xuICAgIGlmICh0eXBlb2YgYXJnID09PSAnc3RyaW5nJykge1xuICAgICAgcmV0dXJuIG5ldyBVUkwoYXJnLCAnZmlsZTovL2xvY2FsaG9zdC8nKS5wYXRobmFtZTtcbiAgICB9XG4gICAgcmV0dXJuIGFyZztcbiAgfVxuXG4gIC8qKlxuICAgKiBDaGVjayBpZiBmaWxlbmFtZSBleGlzdHNcbiAgICovXG4gIGhhc0ZpbGVuYW1lKG5hbWUpIHtcbiAgICByZXR1cm4gdGhpcy4jbWFwRmlsZW5hbWVUb1NBSC5oYXMobmFtZSk7XG4gIH1cblxuICAvKipcbiAgICogR2V0IFNBSCBmb3IgcGF0aFxuICAgKi9cbiAgZ2V0U0FIRm9yUGF0aChwYXRoKSB7XG4gICAgcmV0dXJuIHRoaXMuI21hcEZpbGVuYW1lVG9TQUguZ2V0KHBhdGgpO1xuICB9XG5cbiAgLyoqXG4gICAqIEdldCBuZXh0IGF2YWlsYWJsZSBTQUhcbiAgICovXG4gIG5leHRBdmFpbGFibGVTQUgoKSB7XG4gICAgY29uc3QgW3JjXSA9IHRoaXMuI2F2YWlsYWJsZVNBSC5rZXlzKCk7XG4gICAgcmV0dXJuIHJjO1xuICB9XG5cbiAgLyoqXG4gICAqIERlbGV0ZSBwYXRoIGFzc29jaWF0aW9uXG4gICAqL1xuICBkZWxldGVQYXRoKHBhdGgpIHtcbiAgICBjb25zdCBzYWggPSB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILmdldChwYXRoKTtcbiAgICBpZiAoc2FoKSB7XG4gICAgICB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILmRlbGV0ZShwYXRoKTtcbiAgICAgIHRoaXMuc2V0QXNzb2NpYXRlZFBhdGgoc2FoLCAnJywgMCk7XG4gICAgICByZXR1cm4gdHJ1ZTtcbiAgICB9XG4gICAgcmV0dXJuIGZhbHNlO1xuICB9XG5cbiAgLy8gPT09PT0gVkZTIE1ldGhvZHMgKGNhbGxlZCBmcm9tIEVNX0pTIGhvb2tzKSA9PT09PVxuXG4gIC8qKlxuICAgKiBPcGVuIGEgZmlsZSAtIHJldHVybnMgZmlsZSBJRFxuICAgKi9cbiAgeE9wZW4oZmlsZW5hbWUsIGZsYWdzKSB7XG4gICAgdHJ5IHtcbiAgICAgIGNvbnN0IHBhdGggPSB0aGlzLmdldFBhdGgoZmlsZW5hbWUpO1xuICAgICAgdGhpcy5sb2coYHhPcGVuOiAke3BhdGh9IGZsYWdzPSR7ZmxhZ3N9YCk7XG5cbiAgICAgIGxldCBzYWggPSB0aGlzLmdldFNBSEZvclBhdGgocGF0aCk7XG5cbiAgICAgIGlmICghc2FoICYmIChmbGFncyAmIFNRTElURV9PUEVOX0NSRUFURSkpIHtcbiAgICAgICAgaWYgKHRoaXMuZ2V0RmlsZUNvdW50KCkgPCB0aGlzLmdldENhcGFjaXR5KCkpIHtcbiAgICAgICAgICBzYWggPSB0aGlzLm5leHRBdmFpbGFibGVTQUgoKTtcbiAgICAgICAgICBpZiAoc2FoKSB7XG4gICAgICAgICAgICB0aGlzLnNldEFzc29jaWF0ZWRQYXRoKHNhaCwgcGF0aCwgZmxhZ3MpO1xuICAgICAgICAgIH0gZWxzZSB7XG4gICAgICAgICAgICB0aGlzLmVycm9yKCdObyBhdmFpbGFibGUgU0FIIGluIHBvb2wnKTtcbiAgICAgICAgICAgIHJldHVybiAtMTtcbiAgICAgICAgICB9XG4gICAgICAgIH0gZWxzZSB7XG4gICAgICAgICAgdGhpcy5lcnJvcignU0FIIHBvb2wgaXMgZnVsbCwgY2Fubm90IGNyZWF0ZSBmaWxlJyk7XG4gICAgICAgICAgcmV0dXJuIC0xO1xuICAgICAgICB9XG4gICAgICB9XG5cbiAgICAgIGlmICghc2FoKSB7XG4gICAgICAgIHRoaXMuZXJyb3IoYEZpbGUgbm90IGZvdW5kOiAke3BhdGh9YCk7XG4gICAgICAgIHJldHVybiAtMTtcbiAgICAgIH1cblxuICAgICAgLy8gQWxsb2NhdGUgZmlsZSBJRFxuICAgICAgY29uc3QgZmlsZUlkID0gdGhpcy4jbmV4dEZpbGVJZCsrO1xuICAgICAgdGhpcy4jbWFwRmlsZUlkVG9GaWxlLnNldChmaWxlSWQsIHtcbiAgICAgICAgcGF0aCxcbiAgICAgICAgc2FoLFxuICAgICAgICBmbGFncyxcbiAgICAgICAgbG9ja1R5cGU6IFNRTElURV9MT0NLX05PTkVcbiAgICAgIH0pO1xuXG4gICAgICB0aGlzLmxvZyhgeE9wZW4gc3VjY2VzczogJHtwYXRofSAtPiBmaWxlSWQgJHtmaWxlSWR9YCk7XG4gICAgICByZXR1cm4gZmlsZUlkO1xuXG4gICAgfSBjYXRjaCAoZSkge1xuICAgICAgdGhpcy5lcnJvcigneE9wZW4gZXJyb3I6JywgZSk7XG4gICAgICByZXR1cm4gLTE7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIFJlYWQgZnJvbSBmaWxlXG4gICAqL1xuICB4UmVhZChmaWxlSWQsIGJ1ZmZlciwgYW1vdW50LCBvZmZzZXQpIHtcbiAgICB0cnkge1xuICAgICAgY29uc3QgZmlsZSA9IHRoaXMuI21hcEZpbGVJZFRvRmlsZS5nZXQoZmlsZUlkKTtcbiAgICAgIGlmICghZmlsZSkge1xuICAgICAgICB0aGlzLmVycm9yKGB4UmVhZDogaW52YWxpZCBmaWxlSWQgJHtmaWxlSWR9YCk7XG4gICAgICAgIHJldHVybiBTUUxJVEVfSU9FUlJfUkVBRDtcbiAgICAgIH1cblxuICAgICAgY29uc3QgblJlYWQgPSBmaWxlLnNhaC5yZWFkKGJ1ZmZlciwgeyBhdDogSEVBREVSX09GRlNFVF9EQVRBICsgb2Zmc2V0IH0pO1xuXG4gICAgICBpZiAoblJlYWQgPCBhbW91bnQpIHtcbiAgICAgICAgLy8gU2hvcnQgcmVhZCAtIGZpbGwgcmVzdCB3aXRoIHplcm9zXG4gICAgICAgIGJ1ZmZlci5maWxsKDAsIG5SZWFkKTtcbiAgICAgICAgcmV0dXJuIFNRTElURV9JT0VSUl9TSE9SVF9SRUFEO1xuICAgICAgfVxuXG4gICAgICByZXR1cm4gU1FMSVRFX09LO1xuXG4gICAgfSBjYXRjaCAoZSkge1xuICAgICAgdGhpcy5lcnJvcigneFJlYWQgZXJyb3I6JywgZSk7XG4gICAgICByZXR1cm4gU1FMSVRFX0lPRVJSX1JFQUQ7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIFdyaXRlIHRvIGZpbGVcbiAgICovXG4gIHhXcml0ZShmaWxlSWQsIGJ1ZmZlciwgYW1vdW50LCBvZmZzZXQpIHtcbiAgICB0cnkge1xuICAgICAgY29uc3QgZmlsZSA9IHRoaXMuI21hcEZpbGVJZFRvRmlsZS5nZXQoZmlsZUlkKTtcbiAgICAgIGlmICghZmlsZSkge1xuICAgICAgICB0aGlzLmVycm9yKGB4V3JpdGU6IGludmFsaWQgZmlsZUlkICR7ZmlsZUlkfWApO1xuICAgICAgICByZXR1cm4gU1FMSVRFX0lPRVJSX1dSSVRFO1xuICAgICAgfVxuXG4gICAgICBjb25zdCBuV3JpdHRlbiA9IGZpbGUuc2FoLndyaXRlKGJ1ZmZlciwgeyBhdDogSEVBREVSX09GRlNFVF9EQVRBICsgb2Zmc2V0IH0pO1xuXG4gICAgICBpZiAobldyaXR0ZW4gIT09IGFtb3VudCkge1xuICAgICAgICB0aGlzLmVycm9yKGB4V3JpdGU6IHdyb3RlICR7bldyaXR0ZW59LyR7YW1vdW50fSBieXRlc2ApO1xuICAgICAgICByZXR1cm4gU1FMSVRFX0lPRVJSX1dSSVRFO1xuICAgICAgfVxuXG4gICAgICByZXR1cm4gU1FMSVRFX09LO1xuXG4gICAgfSBjYXRjaCAoZSkge1xuICAgICAgdGhpcy5lcnJvcigneFdyaXRlIGVycm9yOicsIGUpO1xuICAgICAgcmV0dXJuIFNRTElURV9JT0VSUl9XUklURTtcbiAgICB9XG4gIH1cblxuICAvKipcbiAgICogU3luYyBmaWxlIHRvIHN0b3JhZ2VcbiAgICovXG4gIHhTeW5jKGZpbGVJZCwgZmxhZ3MpIHtcbiAgICB0cnkge1xuICAgICAgY29uc3QgZmlsZSA9IHRoaXMuI21hcEZpbGVJZFRvRmlsZS5nZXQoZmlsZUlkKTtcbiAgICAgIGlmICghZmlsZSkge1xuICAgICAgICByZXR1cm4gU1FMSVRFX0lPRVJSO1xuICAgICAgfVxuXG4gICAgICBmaWxlLnNhaC5mbHVzaCgpO1xuICAgICAgcmV0dXJuIFNRTElURV9PSztcblxuICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgIHRoaXMuZXJyb3IoJ3hTeW5jIGVycm9yOicsIGUpO1xuICAgICAgcmV0dXJuIFNRTElURV9JT0VSUjtcbiAgICB9XG4gIH1cblxuICAvKipcbiAgICogVHJ1bmNhdGUgZmlsZVxuICAgKi9cbiAgeFRydW5jYXRlKGZpbGVJZCwgc2l6ZSkge1xuICAgIHRyeSB7XG4gICAgICBjb25zdCBmaWxlID0gdGhpcy4jbWFwRmlsZUlkVG9GaWxlLmdldChmaWxlSWQpO1xuICAgICAgaWYgKCFmaWxlKSB7XG4gICAgICAgIHJldHVybiBTUUxJVEVfSU9FUlI7XG4gICAgICB9XG5cbiAgICAgIGZpbGUuc2FoLnRydW5jYXRlKEhFQURFUl9PRkZTRVRfREFUQSArIHNpemUpO1xuICAgICAgcmV0dXJuIFNRTElURV9PSztcblxuICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgIHRoaXMuZXJyb3IoJ3hUcnVuY2F0ZSBlcnJvcjonLCBlKTtcbiAgICAgIHJldHVybiBTUUxJVEVfSU9FUlI7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIEdldCBmaWxlIHNpemVcbiAgICovXG4gIHhGaWxlU2l6ZShmaWxlSWQpIHtcbiAgICB0cnkge1xuICAgICAgY29uc3QgZmlsZSA9IHRoaXMuI21hcEZpbGVJZFRvRmlsZS5nZXQoZmlsZUlkKTtcbiAgICAgIGlmICghZmlsZSkge1xuICAgICAgICByZXR1cm4gLTE7XG4gICAgICB9XG5cbiAgICAgIGNvbnN0IHNpemUgPSBmaWxlLnNhaC5nZXRTaXplKCkgLSBIRUFERVJfT0ZGU0VUX0RBVEE7XG4gICAgICByZXR1cm4gTWF0aC5tYXgoMCwgc2l6ZSk7XG5cbiAgICB9IGNhdGNoIChlKSB7XG4gICAgICB0aGlzLmVycm9yKCd4RmlsZVNpemUgZXJyb3I6JywgZSk7XG4gICAgICByZXR1cm4gLTE7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIENsb3NlIGZpbGVcbiAgICovXG4gIHhDbG9zZShmaWxlSWQpIHtcbiAgICB0cnkge1xuICAgICAgY29uc3QgZmlsZSA9IHRoaXMuI21hcEZpbGVJZFRvRmlsZS5nZXQoZmlsZUlkKTtcbiAgICAgIGlmICghZmlsZSkge1xuICAgICAgICByZXR1cm4gU1FMSVRFX0VSUk9SO1xuICAgICAgfVxuXG4gICAgICAvLyBEb24ndCBjbG9zZSB0aGUgU0FIIC0gaXQncyByZXVzZWQgaW4gdGhlIHBvb2xcbiAgICAgIC8vIEp1c3QgcmVtb3ZlIGZyb20gb3BlbiBmaWxlcyBtYXBcbiAgICAgIHRoaXMuI21hcEZpbGVJZFRvRmlsZS5kZWxldGUoZmlsZUlkKTtcblxuICAgICAgdGhpcy5sb2coYHhDbG9zZTogZmlsZUlkICR7ZmlsZUlkfSAoJHtmaWxlLnBhdGh9KWApO1xuICAgICAgcmV0dXJuIFNRTElURV9PSztcblxuICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgIHRoaXMuZXJyb3IoJ3hDbG9zZSBlcnJvcjonLCBlKTtcbiAgICAgIHJldHVybiBTUUxJVEVfRVJST1I7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIENoZWNrIGZpbGUgYWNjZXNzXG4gICAqL1xuICB4QWNjZXNzKGZpbGVuYW1lLCBmbGFncykge1xuICAgIHRyeSB7XG4gICAgICBjb25zdCBwYXRoID0gdGhpcy5nZXRQYXRoKGZpbGVuYW1lKTtcbiAgICAgIHJldHVybiB0aGlzLmhhc0ZpbGVuYW1lKHBhdGgpID8gMSA6IDA7XG4gICAgfSBjYXRjaCAoZSkge1xuICAgICAgdGhpcy5lcnJvcigneEFjY2VzcyBlcnJvcjonLCBlKTtcbiAgICAgIHJldHVybiAwO1xuICAgIH1cbiAgfVxuXG4gIC8qKlxuICAgKiBEZWxldGUgZmlsZVxuICAgKi9cbiAgeERlbGV0ZShmaWxlbmFtZSwgc3luY0Rpcikge1xuICAgIHRyeSB7XG4gICAgICBjb25zdCBwYXRoID0gdGhpcy5nZXRQYXRoKGZpbGVuYW1lKTtcbiAgICAgIHRoaXMubG9nKGB4RGVsZXRlOiAke3BhdGh9YCk7XG4gICAgICB0aGlzLmRlbGV0ZVBhdGgocGF0aCk7XG4gICAgICByZXR1cm4gU1FMSVRFX09LO1xuICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgIHRoaXMuZXJyb3IoJ3hEZWxldGUgZXJyb3I6JywgZSk7XG4gICAgICByZXR1cm4gU1FMSVRFX0lPRVJSO1xuICAgIH1cbiAgfVxufVxuXG4vLyBFeHBvcnQgc2luZ2xldG9uIGluc3RhbmNlXG5jb25zdCBvcGZzU0FIUG9vbCA9IG5ldyBPcGZzU0FIUG9vbCh7XG4gIGRpcmVjdG9yeTogJy5vcGZzLXNhaHBvb2wnLFxuICBpbml0aWFsQ2FwYWNpdHk6IDYsXG4gIGNsZWFyT25Jbml0OiBmYWxzZVxufSk7XG5cbi8vIE1ha2UgYXZhaWxhYmxlIGdsb2JhbGx5IGZvciBFTV9KUyBob29rc1xuaWYgKHR5cGVvZiBnbG9iYWxUaGlzICE9PSAndW5kZWZpbmVkJykge1xuICBnbG9iYWxUaGlzLm9wZnNTQUhQb29sID0gb3Bmc1NBSFBvb2w7XG59XG5cbi8vIEVTNiBtb2R1bGUgZXhwb3J0XG5leHBvcnQgeyBPcGZzU0FIUG9vbCwgb3Bmc1NBSFBvb2wgfTtcbiIsICIvLyBvcGZzLXdvcmtlci50c1xuLy8gV2ViIFdvcmtlciBmb3IgT1BGUyBmaWxlIEkvTyB1c2luZyBTQUhQb29sXG4vLyBIYW5kbGVzIG9ubHkgZmlsZSByZWFkL3dyaXRlIG9wZXJhdGlvbnMgLSBubyBTUUwgZXhlY3V0aW9uXG5cbmltcG9ydCB7IG9wZnNTQUhQb29sIH0gZnJvbSAnLi9vcGZzLXNhaHBvb2wnO1xuXG5pbnRlcmZhY2UgV29ya2VyTWVzc2FnZSB7XG4gICAgaWQ6IG51bWJlcjtcbiAgICB0eXBlOiBzdHJpbmc7XG4gICAgYXJncz86IGFueTtcbn1cblxuaW50ZXJmYWNlIFdvcmtlclJlc3BvbnNlIHtcbiAgICBpZDogbnVtYmVyO1xuICAgIHN1Y2Nlc3M6IGJvb2xlYW47XG4gICAgcmVzdWx0PzogYW55O1xuICAgIGVycm9yPzogc3RyaW5nO1xufVxuXG4vLyBXYWl0IGZvciBPUEZTIFNBSFBvb2wgdG8gaW5pdGlhbGl6ZVxub3Bmc1NBSFBvb2wuaXNSZWFkeS50aGVuKCgpID0+IHtcbiAgICBjb25zb2xlLmxvZygnW09QRlMgV29ya2VyXSBTQUhQb29sIGluaXRpYWxpemVkLCBzZW5kaW5nIHJlYWR5IHNpZ25hbCcpO1xuICAgIHNlbGYucG9zdE1lc3NhZ2UoeyB0eXBlOiAncmVhZHknIH0pO1xufSkuY2F0Y2goKGVycm9yKSA9PiB7XG4gICAgY29uc29sZS5lcnJvcignW09QRlMgV29ya2VyXSBTQUhQb29sIGluaXRpYWxpemF0aW9uIGZhaWxlZDonLCBlcnJvcik7XG4gICAgc2VsZi5wb3N0TWVzc2FnZSh7IHR5cGU6ICdlcnJvcicsIGVycm9yOiBlcnJvci5tZXNzYWdlIH0pO1xufSk7XG5cbi8vIEhhbmRsZSBtZXNzYWdlcyBmcm9tIG1haW4gdGhyZWFkXG5zZWxmLm9ubWVzc2FnZSA9IGFzeW5jIChldmVudDogTWVzc2FnZUV2ZW50PFdvcmtlck1lc3NhZ2U+KSA9PiB7XG4gICAgY29uc3QgeyBpZCwgdHlwZSwgYXJncyB9ID0gZXZlbnQuZGF0YTtcblxuICAgIHRyeSB7XG4gICAgICAgIGxldCByZXN1bHQ6IGFueTtcblxuICAgICAgICBzd2l0Y2ggKHR5cGUpIHtcbiAgICAgICAgICAgIGNhc2UgJ2NsZWFudXAnOlxuICAgICAgICAgICAgICAgIC8vIFJlbGVhc2UgYWxsIE9QRlMgaGFuZGxlcyBiZWZvcmUgcGFnZSB1bmxvYWRcbiAgICAgICAgICAgICAgICBjb25zb2xlLmxvZygnW09QRlMgV29ya2VyXSBDbGVhbmluZyB1cCBoYW5kbGVzIGJlZm9yZSB1bmxvYWQuLi4nKTtcbiAgICAgICAgICAgICAgICBvcGZzU0FIUG9vbC5yZWxlYXNlQWNjZXNzSGFuZGxlcygpO1xuICAgICAgICAgICAgICAgIGNvbnNvbGUubG9nKCdbT1BGUyBXb3JrZXJdIENsZWFudXAgY29tcGxldGUnKTtcbiAgICAgICAgICAgICAgICByZXN1bHQgPSB7IHN1Y2Nlc3M6IHRydWUgfTtcbiAgICAgICAgICAgICAgICBicmVhaztcblxuICAgICAgICAgICAgY2FzZSAnZ2V0Q2FwYWNpdHknOlxuICAgICAgICAgICAgICAgIHJlc3VsdCA9IHtcbiAgICAgICAgICAgICAgICAgICAgY2FwYWNpdHk6IG9wZnNTQUhQb29sLmdldENhcGFjaXR5KClcbiAgICAgICAgICAgICAgICB9O1xuICAgICAgICAgICAgICAgIGJyZWFrO1xuXG4gICAgICAgICAgICBjYXNlICdhZGRDYXBhY2l0eSc6XG4gICAgICAgICAgICAgICAgcmVzdWx0ID0ge1xuICAgICAgICAgICAgICAgICAgICBuZXdDYXBhY2l0eTogYXdhaXQgb3Bmc1NBSFBvb2wuYWRkQ2FwYWNpdHkoYXJncy5jb3VudClcbiAgICAgICAgICAgICAgICB9O1xuICAgICAgICAgICAgICAgIGJyZWFrO1xuXG4gICAgICAgICAgICBjYXNlICdnZXRGaWxlTGlzdCc6XG4gICAgICAgICAgICAgICAgcmVzdWx0ID0ge1xuICAgICAgICAgICAgICAgICAgICBmaWxlczogb3Bmc1NBSFBvb2wuZ2V0RmlsZU5hbWVzKClcbiAgICAgICAgICAgICAgICB9O1xuICAgICAgICAgICAgICAgIGJyZWFrO1xuXG4gICAgICAgICAgICBjYXNlICdyZWFkRmlsZSc6XG4gICAgICAgICAgICAgICAgLy8gUmVhZCBmaWxlIGZyb20gT1BGUyB1c2luZyBTQUhQb29sXG4gICAgICAgICAgICAgICAgY29uc3QgZmlsZUlkID0gb3Bmc1NBSFBvb2wueE9wZW4oYXJncy5maWxlbmFtZSwgMHgwMSk7IC8vIFJFQURPTkxZXG4gICAgICAgICAgICAgICAgaWYgKGZpbGVJZCA8IDApIHtcbiAgICAgICAgICAgICAgICAgICAgdGhyb3cgbmV3IEVycm9yKGBGaWxlIG5vdCBmb3VuZDogJHthcmdzLmZpbGVuYW1lfWApO1xuICAgICAgICAgICAgICAgIH1cblxuICAgICAgICAgICAgICAgIGNvbnN0IHNpemUgPSBvcGZzU0FIUG9vbC54RmlsZVNpemUoZmlsZUlkKTtcbiAgICAgICAgICAgICAgICBjb25zdCBidWZmZXIgPSBuZXcgVWludDhBcnJheShzaXplKTtcbiAgICAgICAgICAgICAgICBjb25zdCByZWFkUmVzdWx0ID0gb3Bmc1NBSFBvb2wueFJlYWQoZmlsZUlkLCBidWZmZXIsIHNpemUsIDApO1xuICAgICAgICAgICAgICAgIG9wZnNTQUhQb29sLnhDbG9zZShmaWxlSWQpO1xuXG4gICAgICAgICAgICAgICAgaWYgKHJlYWRSZXN1bHQgIT09IDApIHtcbiAgICAgICAgICAgICAgICAgICAgdGhyb3cgbmV3IEVycm9yKGBGYWlsZWQgdG8gcmVhZCBmaWxlOiAke2FyZ3MuZmlsZW5hbWV9YCk7XG4gICAgICAgICAgICAgICAgfVxuXG4gICAgICAgICAgICAgICAgcmVzdWx0ID0ge1xuICAgICAgICAgICAgICAgICAgICBkYXRhOiBBcnJheS5mcm9tKGJ1ZmZlcilcbiAgICAgICAgICAgICAgICB9O1xuICAgICAgICAgICAgICAgIGJyZWFrO1xuXG4gICAgICAgICAgICBjYXNlICd3cml0ZUZpbGUnOlxuICAgICAgICAgICAgICAgIC8vIFdyaXRlIGZpbGUgdG8gT1BGUyB1c2luZyBTQUhQb29sXG4gICAgICAgICAgICAgICAgY29uc3QgZGF0YSA9IG5ldyBVaW50OEFycmF5KGFyZ3MuZGF0YSk7XG4gICAgICAgICAgICAgICAgY29uc3Qgd3JpdGVGaWxlSWQgPSBvcGZzU0FIUG9vbC54T3BlbihcbiAgICAgICAgICAgICAgICAgICAgYXJncy5maWxlbmFtZSxcbiAgICAgICAgICAgICAgICAgICAgMHgwMiB8IDB4MDQgfCAweDEwMCAvLyBSRUFEV1JJVEUgfCBDUkVBVEUgfCBNQUlOX0RCXG4gICAgICAgICAgICAgICAgKTtcblxuICAgICAgICAgICAgICAgIGlmICh3cml0ZUZpbGVJZCA8IDApIHtcbiAgICAgICAgICAgICAgICAgICAgdGhyb3cgbmV3IEVycm9yKGBGYWlsZWQgdG8gb3BlbiBmaWxlIGZvciB3cml0aW5nOiAke2FyZ3MuZmlsZW5hbWV9YCk7XG4gICAgICAgICAgICAgICAgfVxuXG4gICAgICAgICAgICAgICAgLy8gVHJ1bmNhdGUgdG8gZXhhY3Qgc2l6ZVxuICAgICAgICAgICAgICAgIG9wZnNTQUhQb29sLnhUcnVuY2F0ZSh3cml0ZUZpbGVJZCwgZGF0YS5sZW5ndGgpO1xuXG4gICAgICAgICAgICAgICAgLy8gV3JpdGUgZGF0YVxuICAgICAgICAgICAgICAgIGNvbnN0IHdyaXRlUmVzdWx0ID0gb3Bmc1NBSFBvb2wueFdyaXRlKHdyaXRlRmlsZUlkLCBkYXRhLCBkYXRhLmxlbmd0aCwgMCk7XG5cbiAgICAgICAgICAgICAgICAvLyBTeW5jIHRvIGRpc2tcbiAgICAgICAgICAgICAgICBvcGZzU0FIUG9vbC54U3luYyh3cml0ZUZpbGVJZCwgMCk7XG4gICAgICAgICAgICAgICAgb3Bmc1NBSFBvb2wueENsb3NlKHdyaXRlRmlsZUlkKTtcblxuICAgICAgICAgICAgICAgIGlmICh3cml0ZVJlc3VsdCAhPT0gMCkge1xuICAgICAgICAgICAgICAgICAgICB0aHJvdyBuZXcgRXJyb3IoYEZhaWxlZCB0byB3cml0ZSBmaWxlOiAke2FyZ3MuZmlsZW5hbWV9YCk7XG4gICAgICAgICAgICAgICAgfVxuXG4gICAgICAgICAgICAgICAgcmVzdWx0ID0ge1xuICAgICAgICAgICAgICAgICAgICBieXRlc1dyaXR0ZW46IGRhdGEubGVuZ3RoXG4gICAgICAgICAgICAgICAgfTtcbiAgICAgICAgICAgICAgICBicmVhaztcblxuICAgICAgICAgICAgY2FzZSAncGVyc2lzdERpcnR5UGFnZXMnOlxuICAgICAgICAgICAgICAgIC8vIFdyaXRlIG9ubHkgZGlydHkgcGFnZXMgdG8gT1BGUyAoaW5jcmVtZW50YWwgc3luYylcbiAgICAgICAgICAgICAgICBjb25zdCB7IGZpbGVuYW1lLCBwYWdlcyB9ID0gYXJncztcblxuICAgICAgICAgICAgICAgIGlmICghcGFnZXMgfHwgcGFnZXMubGVuZ3RoID09PSAwKSB7XG4gICAgICAgICAgICAgICAgICAgIHJlc3VsdCA9IHsgcGFnZXNXcml0dGVuOiAwIH07XG4gICAgICAgICAgICAgICAgICAgIGJyZWFrO1xuICAgICAgICAgICAgICAgIH1cblxuICAgICAgICAgICAgICAgIGNvbnNvbGUubG9nKGBbT1BGUyBXb3JrZXJdIFBlcnNpc3RpbmcgJHtwYWdlcy5sZW5ndGh9IGRpcnR5IHBhZ2VzIGZvciAke2ZpbGVuYW1lfWApO1xuXG4gICAgICAgICAgICAgICAgY29uc3QgUEFHRV9TSVpFID0gNDA5NjtcbiAgICAgICAgICAgICAgICBjb25zdCBTUUxJVEVfT0sgPSAwO1xuICAgICAgICAgICAgICAgIGNvbnN0IEZMQUdTX1JFQURXUklURSA9IDB4MDI7XG4gICAgICAgICAgICAgICAgY29uc3QgRkxBR1NfTUFJTl9EQiA9IDB4MTAwO1xuXG4gICAgICAgICAgICAgICAgLy8gT3BlbiBmaWxlIGZvciBwYXJ0aWFsIHdyaXRlc1xuICAgICAgICAgICAgICAgIGNvbnN0IHBhcnRpYWxGaWxlSWQgPSBvcGZzU0FIUG9vbC54T3BlbihcbiAgICAgICAgICAgICAgICAgICAgZmlsZW5hbWUsXG4gICAgICAgICAgICAgICAgICAgIEZMQUdTX1JFQURXUklURSB8IEZMQUdTX01BSU5fREJcbiAgICAgICAgICAgICAgICApO1xuXG4gICAgICAgICAgICAgICAgaWYgKHBhcnRpYWxGaWxlSWQgPCAwKSB7XG4gICAgICAgICAgICAgICAgICAgIHRocm93IG5ldyBFcnJvcihgRmFpbGVkIHRvIG9wZW4gZmlsZSBmb3IgcGFydGlhbCB3cml0ZTogJHtmaWxlbmFtZX1gKTtcbiAgICAgICAgICAgICAgICB9XG5cbiAgICAgICAgICAgICAgICBsZXQgcGFnZXNXcml0dGVuID0gMDtcblxuICAgICAgICAgICAgICAgIHRyeSB7XG4gICAgICAgICAgICAgICAgICAgIC8vIFdyaXRlIGVhY2ggZGlydHkgcGFnZVxuICAgICAgICAgICAgICAgICAgICBmb3IgKGNvbnN0IHBhZ2Ugb2YgcGFnZXMpIHtcbiAgICAgICAgICAgICAgICAgICAgICAgIGNvbnN0IHsgcGFnZU51bWJlciwgZGF0YSB9ID0gcGFnZTtcbiAgICAgICAgICAgICAgICAgICAgICAgIGNvbnN0IG9mZnNldCA9IHBhZ2VOdW1iZXIgKiBQQUdFX1NJWkU7XG4gICAgICAgICAgICAgICAgICAgICAgICBjb25zdCBwYWdlQnVmZmVyID0gbmV3IFVpbnQ4QXJyYXkoZGF0YSk7XG5cbiAgICAgICAgICAgICAgICAgICAgICAgIGNvbnN0IHdyaXRlUmMgPSBvcGZzU0FIUG9vbC54V3JpdGUoXG4gICAgICAgICAgICAgICAgICAgICAgICAgICAgcGFydGlhbEZpbGVJZCxcbiAgICAgICAgICAgICAgICAgICAgICAgICAgICBwYWdlQnVmZmVyLFxuICAgICAgICAgICAgICAgICAgICAgICAgICAgIHBhZ2VCdWZmZXIubGVuZ3RoLFxuICAgICAgICAgICAgICAgICAgICAgICAgICAgIG9mZnNldFxuICAgICAgICAgICAgICAgICAgICAgICAgKTtcblxuICAgICAgICAgICAgICAgICAgICAgICAgaWYgKHdyaXRlUmMgIT09IFNRTElURV9PSykge1xuICAgICAgICAgICAgICAgICAgICAgICAgICAgIHRocm93IG5ldyBFcnJvcihgRmFpbGVkIHRvIHdyaXRlIHBhZ2UgJHtwYWdlTnVtYmVyfSBhdCBvZmZzZXQgJHtvZmZzZXR9YCk7XG4gICAgICAgICAgICAgICAgICAgICAgICB9XG5cbiAgICAgICAgICAgICAgICAgICAgICAgIHBhZ2VzV3JpdHRlbisrO1xuICAgICAgICAgICAgICAgICAgICB9XG5cbiAgICAgICAgICAgICAgICAgICAgLy8gU3luYyB0byBlbnN1cmUgZGF0YSBpcyBwZXJzaXN0ZWRcbiAgICAgICAgICAgICAgICAgICAgb3Bmc1NBSFBvb2wueFN5bmMocGFydGlhbEZpbGVJZCwgMCk7XG5cbiAgICAgICAgICAgICAgICAgICAgY29uc29sZS5sb2coYFtPUEZTIFdvcmtlcl0gU3VjY2Vzc2Z1bGx5IHdyb3RlICR7cGFnZXNXcml0dGVufSBwYWdlc2ApO1xuXG4gICAgICAgICAgICAgICAgfSBmaW5hbGx5IHtcbiAgICAgICAgICAgICAgICAgICAgLy8gQWx3YXlzIGNsb3NlIHRoZSBmaWxlXG4gICAgICAgICAgICAgICAgICAgIG9wZnNTQUhQb29sLnhDbG9zZShwYXJ0aWFsRmlsZUlkKTtcbiAgICAgICAgICAgICAgICB9XG5cbiAgICAgICAgICAgICAgICByZXN1bHQgPSB7XG4gICAgICAgICAgICAgICAgICAgIHBhZ2VzV3JpdHRlbixcbiAgICAgICAgICAgICAgICAgICAgYnl0ZXNXcml0dGVuOiBwYWdlc1dyaXR0ZW4gKiBQQUdFX1NJWkVcbiAgICAgICAgICAgICAgICB9O1xuICAgICAgICAgICAgICAgIGJyZWFrO1xuXG4gICAgICAgICAgICBjYXNlICdkZWxldGVGaWxlJzpcbiAgICAgICAgICAgICAgICBjb25zdCBkZWxldGVSZXN1bHQgPSBvcGZzU0FIUG9vbC54RGVsZXRlKGFyZ3MuZmlsZW5hbWUsIDEpO1xuICAgICAgICAgICAgICAgIGlmIChkZWxldGVSZXN1bHQgIT09IDApIHtcbiAgICAgICAgICAgICAgICAgICAgdGhyb3cgbmV3IEVycm9yKGBGYWlsZWQgdG8gZGVsZXRlIGZpbGU6ICR7YXJncy5maWxlbmFtZX1gKTtcbiAgICAgICAgICAgICAgICB9XG4gICAgICAgICAgICAgICAgcmVzdWx0ID0geyBzdWNjZXNzOiB0cnVlIH07XG4gICAgICAgICAgICAgICAgYnJlYWs7XG5cbiAgICAgICAgICAgIGNhc2UgJ2ZpbGVFeGlzdHMnOlxuICAgICAgICAgICAgICAgIGNvbnN0IGV4aXN0cyA9IG9wZnNTQUhQb29sLnhBY2Nlc3MoYXJncy5maWxlbmFtZSwgMCkgPT09IDA7XG4gICAgICAgICAgICAgICAgcmVzdWx0ID0geyBleGlzdHMgfTtcbiAgICAgICAgICAgICAgICBicmVhaztcblxuICAgICAgICAgICAgZGVmYXVsdDpcbiAgICAgICAgICAgICAgICB0aHJvdyBuZXcgRXJyb3IoYFVua25vd24gbWVzc2FnZSB0eXBlOiAke3R5cGV9YCk7XG4gICAgICAgIH1cblxuICAgICAgICBjb25zdCByZXNwb25zZTogV29ya2VyUmVzcG9uc2UgPSB7XG4gICAgICAgICAgICBpZCxcbiAgICAgICAgICAgIHN1Y2Nlc3M6IHRydWUsXG4gICAgICAgICAgICByZXN1bHRcbiAgICAgICAgfTtcbiAgICAgICAgc2VsZi5wb3N0TWVzc2FnZShyZXNwb25zZSk7XG5cbiAgICB9IGNhdGNoIChlcnJvcikge1xuICAgICAgICBjb25zdCByZXNwb25zZTogV29ya2VyUmVzcG9uc2UgPSB7XG4gICAgICAgICAgICBpZCxcbiAgICAgICAgICAgIHN1Y2Nlc3M6IGZhbHNlLFxuICAgICAgICAgICAgZXJyb3I6IGVycm9yIGluc3RhbmNlb2YgRXJyb3IgPyBlcnJvci5tZXNzYWdlIDogJ1Vua25vd24gZXJyb3InXG4gICAgICAgIH07XG4gICAgICAgIHNlbGYucG9zdE1lc3NhZ2UocmVzcG9uc2UpO1xuICAgIH1cbn07XG5cbmNvbnNvbGUubG9nKCdbT1BGUyBXb3JrZXJdIFdvcmtlciBzY3JpcHQgbG9hZGVkLCB3YWl0aW5nIGZvciBTQUhQb29sIGluaXRpYWxpemF0aW9uLi4uJyk7XG4iXSwKICAibWFwcGluZ3MiOiAiO0FBV0EsSUFBTSxjQUFjO0FBQ3BCLElBQU0sdUJBQXVCO0FBQzdCLElBQU0sb0JBQW9CO0FBQzFCLElBQU0scUJBQXFCO0FBQzNCLElBQU0scUJBQXFCLHVCQUF1QjtBQUNsRCxJQUFNLHNCQUFzQjtBQUM1QixJQUFNLHVCQUF1QjtBQUM3QixJQUFNLHFCQUFxQjtBQUczQixJQUFNLHNCQUFzQjtBQUM1QixJQUFNLDJCQUEyQjtBQUNqQyxJQUFNLDRCQUE0QjtBQUNsQyxJQUFNLGtCQUFrQjtBQUN4QixJQUFNLHFCQUFxQjtBQUMzQixJQUFNLDRCQUE0QjtBQUNsQyxJQUFNLHFCQUFxQjtBQUUzQixJQUFNLHdCQUNKLHNCQUNBLDJCQUNBLDRCQUNBO0FBRUYsSUFBTSx5QkFBeUI7QUFDL0IsSUFB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
