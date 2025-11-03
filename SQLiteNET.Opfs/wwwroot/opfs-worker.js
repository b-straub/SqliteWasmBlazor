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
    // Log level (matches OpfsLogLevel enum: None=0, Error=1, Warning=2, Info=3, Debug=4)
    this.logLevel = 2;
    // Default: Warning
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
    if (this.logLevel >= 3) {
      console.log("[OpfsSAHPool]", ...args);
    }
  }
  warn(...args) {
    if (this.logLevel >= 2) {
      console.warn("[OpfsSAHPool]", ...args);
    }
  }
  error(...args) {
    if (this.logLevel >= 1) {
      console.error("[OpfsSAHPool]", ...args);
    }
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
        if (flags & SQLITE_OPEN_CREATE) {
          this.error(`File not found: ${path}`);
        } else {
          this.log(`File not found (READONLY open): ${path}`);
        }
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
var workerLogLevel = 2 /* Warning */;
var log = {
  debug: (...args) => workerLogLevel >= 4 /* Debug */ && console.log("[OPFS Worker]", ...args),
  info: (...args) => workerLogLevel >= 3 /* Info */ && console.log("[OPFS Worker] \u2713", ...args),
  warn: (...args) => workerLogLevel >= 2 /* Warning */ && console.warn("[OPFS Worker] \u26A0", ...args),
  error: (...args) => workerLogLevel >= 1 /* Error */ && console.error("[OPFS Worker] \u274C", ...args)
};
opfsSAHPool.isReady.then(() => {
  log.info("SAHPool initialized, sending ready signal");
  self.postMessage({ type: "ready" });
}).catch((error) => {
  log.error("SAHPool initialization failed:", error);
  self.postMessage({ type: "error", error: error.message });
});
self.onmessage = async (event) => {
  const { id, type, args } = event.data;
  try {
    let result;
    switch (type) {
      case "setLogLevel":
        workerLogLevel = args.level;
        opfsSAHPool.logLevel = args.level;
        log.info(`Log level set to ${args.level}`);
        result = { success: true };
        break;
      case "cleanup":
        log.info("Cleaning up handles before unload...");
        opfsSAHPool.releaseAccessHandles();
        log.info("Cleanup complete");
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
          log.debug(`File not found in OPFS: ${args.filename} (will be created on first write)`);
          result = {
            data: []
            // Return empty array to indicate file doesn't exist
          };
          break;
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
        log.info(`Persisting ${pages.length} dirty pages for ${filename}`);
        const PAGE_SIZE = 4096;
        const SQLITE_OK2 = 0;
        const FLAGS_READWRITE = 2;
        const FLAGS_CREATE = 4;
        const FLAGS_MAIN_DB = 256;
        const partialFileId = opfsSAHPool.xOpen(
          filename,
          FLAGS_READWRITE | FLAGS_CREATE | FLAGS_MAIN_DB
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
          log.info(`Successfully wrote ${pagesWritten} pages`);
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
log.info("Worker script loaded, waiting for SAHPool initialization...");
//# sourceMappingURL=data:application/json;base64,ewogICJ2ZXJzaW9uIjogMywKICAic291cmNlcyI6IFsiLi4vVHlwZXNjcmlwdC9vcGZzLXNhaHBvb2wudHMiLCAiLi4vVHlwZXNjcmlwdC9vcGZzLXdvcmtlci50cyJdLAogICJzb3VyY2VzQ29udGVudCI6IFsiLyoqXG4gKiBvcGZzLXNhaHBvb2wuanMgLSBTdGFuZGFsb25lIE9wZnNTQUhQb29sIFZGUyBJbXBsZW1lbnRhdGlvblxuICpcbiAqIEJhc2VkIG9uIEBzcWxpdGUub3JnL3NxbGl0ZS13YXNtJ3MgT3Bmc1NBSFBvb2wgYnkgdGhlIFNRTGl0ZSB0ZWFtXG4gKiBXaGljaCBpcyBiYXNlZCBvbiBSb3kgSGFzaGltb3RvJ3MgQWNjZXNzSGFuZGxlUG9vbFZGU1xuICpcbiAqIFRoaXMgaXMgYSBzaW1wbGlmaWVkIHZlcnNpb24gZm9yIGRpcmVjdCBpbnRlZ3JhdGlvbiB3aXRoIG91ciBjdXN0b20gZV9zcWxpdGUzX2pzdmZzLmFcbiAqIE5vIHdvcmtlciBtZXNzYWdpbmcgLSBydW5zIGRpcmVjdGx5IGluIHRoZSB3b3JrZXIgY29udGV4dCB3aXRoIG91ciBXQVNNIG1vZHVsZS5cbiAqL1xuXG4vLyBDb25zdGFudHMgbWF0Y2hpbmcgU1FMaXRlIFZGUyByZXF1aXJlbWVudHNcbmNvbnN0IFNFQ1RPUl9TSVpFID0gNDA5NjtcbmNvbnN0IEhFQURFUl9NQVhfUEFUSF9TSVpFID0gNTEyO1xuY29uc3QgSEVBREVSX0ZMQUdTX1NJWkUgPSA0O1xuY29uc3QgSEVBREVSX0RJR0VTVF9TSVpFID0gODtcbmNvbnN0IEhFQURFUl9DT1JQVVNfU0laRSA9IEhFQURFUl9NQVhfUEFUSF9TSVpFICsgSEVBREVSX0ZMQUdTX1NJWkU7XG5jb25zdCBIRUFERVJfT0ZGU0VUX0ZMQUdTID0gSEVBREVSX01BWF9QQVRIX1NJWkU7XG5jb25zdCBIRUFERVJfT0ZGU0VUX0RJR0VTVCA9IEhFQURFUl9DT1JQVVNfU0laRTtcbmNvbnN0IEhFQURFUl9PRkZTRVRfREFUQSA9IFNFQ1RPUl9TSVpFO1xuXG4vLyBTUUxpdGUgZmlsZSB0eXBlIGZsYWdzXG5jb25zdCBTUUxJVEVfT1BFTl9NQUlOX0RCID0gMHgwMDAwMDEwMDtcbmNvbnN0IFNRTElURV9PUEVOX01BSU5fSk9VUk5BTCA9IDB4MDAwMDA4MDA7XG5jb25zdCBTUUxJVEVfT1BFTl9TVVBFUl9KT1VSTkFMID0gMHgwMDAwNDAwMDtcbmNvbnN0IFNRTElURV9PUEVOX1dBTCA9IDB4MDAwODAwMDA7XG5jb25zdCBTUUxJVEVfT1BFTl9DUkVBVEUgPSAweDAwMDAwMDA0O1xuY29uc3QgU1FMSVRFX09QRU5fREVMRVRFT05DTE9TRSA9IDB4MDAwMDAwMDg7XG5jb25zdCBTUUxJVEVfT1BFTl9NRU1PUlkgPSAweDAwMDAwMDgwOyAvLyBVc2VkIGFzIEZMQUdfQ09NUFVURV9ESUdFU1RfVjJcblxuY29uc3QgUEVSU0lTVEVOVF9GSUxFX1RZUEVTID1cbiAgU1FMSVRFX09QRU5fTUFJTl9EQiB8XG4gIFNRTElURV9PUEVOX01BSU5fSk9VUk5BTCB8XG4gIFNRTElURV9PUEVOX1NVUEVSX0pPVVJOQUwgfFxuICBTUUxJVEVfT1BFTl9XQUw7XG5cbmNvbnN0IEZMQUdfQ09NUFVURV9ESUdFU1RfVjIgPSBTUUxJVEVfT1BFTl9NRU1PUlk7XG5jb25zdCBPUEFRVUVfRElSX05BTUUgPSAnLm9wYXF1ZSc7XG5cbi8vIFNRTGl0ZSByZXN1bHQgY29kZXNcbmNvbnN0IFNRTElURV9PSyA9IDA7XG5jb25zdCBTUUxJVEVfRVJST1IgPSAxO1xuY29uc3QgU1FMSVRFX0lPRVJSID0gMTA7XG5jb25zdCBTUUxJVEVfSU9FUlJfU0hPUlRfUkVBRCA9IDUyMjsgLy8gU1FMSVRFX0lPRVJSIHwgKDI8PDgpXG5jb25zdCBTUUxJVEVfSU9FUlJfV1JJVEUgPSA3Nzg7IC8vIFNRTElURV9JT0VSUiB8ICgzPDw4KVxuY29uc3QgU1FMSVRFX0lPRVJSX1JFQUQgPSAyNjY7IC8vIFNRTElURV9JT0VSUiB8ICgxPDw4KVxuY29uc3QgU1FMSVRFX0NBTlRPUEVOID0gMTQ7XG5jb25zdCBTUUxJVEVfTE9DS19OT05FID0gMDtcblxuY29uc3QgZ2V0UmFuZG9tTmFtZSA9ICgpID0+IE1hdGgucmFuZG9tKCkudG9TdHJpbmcoMzYpLnNsaWNlKDIpO1xuY29uc3QgdGV4dERlY29kZXIgPSBuZXcgVGV4dERlY29kZXIoKTtcbmNvbnN0IHRleHRFbmNvZGVyID0gbmV3IFRleHRFbmNvZGVyKCk7XG5cbi8qKlxuICogT3Bmc1NBSFBvb2wgLSBQb29sLWJhc2VkIE9QRlMgVkZTIHdpdGggU3luY2hyb25vdXMgQWNjZXNzIEhhbmRsZXNcbiAqXG4gKiBNYW5hZ2VzIGEgcG9vbCBvZiBwcmUtYWxsb2NhdGVkIE9QRlMgZmlsZXMgd2l0aCBzeW5jaHJvbm91cyBhY2Nlc3MgaGFuZGxlcy5cbiAqIEZpbGVzIGFyZSBzdG9yZWQgd2l0aCBhIDQwOTYtYnl0ZSBoZWFkZXIgY29udGFpbmluZyBtZXRhZGF0YS5cbiAqL1xuY2xhc3MgT3Bmc1NBSFBvb2wge1xuICAjZGhWZnNSb290ID0gbnVsbDtcbiAgI2RoT3BhcXVlID0gbnVsbDtcbiAgI2RoVmZzUGFyZW50ID0gbnVsbDtcblxuICAvLyBQb29sIG1hbmFnZW1lbnRcbiAgI21hcFNBSFRvTmFtZSA9IG5ldyBNYXAoKTsgICAgICAgLy8gU0FIIC0+IHJhbmRvbSBPUEZTIGZpbGVuYW1lXG4gICNtYXBGaWxlbmFtZVRvU0FIID0gbmV3IE1hcCgpOyAgIC8vIFNRTGl0ZSBwYXRoIC0+IFNBSFxuICAjYXZhaWxhYmxlU0FIID0gbmV3IFNldCgpOyAgICAgICAvLyBVbmFzc29jaWF0ZWQgU0FIcyByZWFkeSBmb3IgdXNlXG5cbiAgLy8gRmlsZSBoYW5kbGUgdHJhY2tpbmcgZm9yIG9wZW4gZmlsZXNcbiAgI21hcEZpbGVJZFRvRmlsZSA9IG5ldyBNYXAoKTsgICAgLy8gZmlsZUlkIC0+IHtwYXRoLCBzYWgsIGxvY2tUeXBlLCBmbGFnc31cbiAgI25leHRGaWxlSWQgPSAxO1xuXG4gIC8vIEhlYWRlciBidWZmZXIgZm9yIHJlYWRpbmcvd3JpdGluZyBmaWxlIG1ldGFkYXRhXG4gICNhcEJvZHkgPSBuZXcgVWludDhBcnJheShIRUFERVJfQ09SUFVTX1NJWkUpO1xuICAjZHZCb2R5ID0gbnVsbDtcblxuICAvLyBMb2cgbGV2ZWwgKG1hdGNoZXMgT3Bmc0xvZ0xldmVsIGVudW06IE5vbmU9MCwgRXJyb3I9MSwgV2FybmluZz0yLCBJbmZvPTMsIERlYnVnPTQpXG4gIGxvZ0xldmVsID0gMjsgIC8vIERlZmF1bHQ6IFdhcm5pbmdcblxuICB2ZnNEaXIgPSBudWxsO1xuXG4gIGNvbnN0cnVjdG9yKG9wdGlvbnMgPSB7fSkge1xuICAgIHRoaXMudmZzRGlyID0gb3B0aW9ucy5kaXJlY3RvcnkgfHwgJy5vcGZzLXNhaHBvb2wnO1xuICAgIHRoaXMuI2R2Qm9keSA9IG5ldyBEYXRhVmlldyh0aGlzLiNhcEJvZHkuYnVmZmVyLCB0aGlzLiNhcEJvZHkuYnl0ZU9mZnNldCk7XG4gICAgdGhpcy5pc1JlYWR5ID0gdGhpcy5yZXNldChvcHRpb25zLmNsZWFyT25Jbml0IHx8IGZhbHNlKVxuICAgICAgLnRoZW4oKCkgPT4ge1xuICAgICAgICBjb25zdCBjYXBhY2l0eSA9IHRoaXMuZ2V0Q2FwYWNpdHkoKTtcbiAgICAgICAgaWYgKGNhcGFjaXR5ID4gMCkge1xuICAgICAgICAgIHJldHVybiBQcm9taXNlLnJlc29sdmUoKTtcbiAgICAgICAgfVxuICAgICAgICByZXR1cm4gdGhpcy5hZGRDYXBhY2l0eShvcHRpb25zLmluaXRpYWxDYXBhY2l0eSB8fCA2KTtcbiAgICAgIH0pO1xuICB9XG5cbiAgbG9nKC4uLmFyZ3MpIHtcbiAgICBpZiAodGhpcy5sb2dMZXZlbCA+PSAzKSB7ICAvLyBJbmZvIGxldmVsXG4gICAgICBjb25zb2xlLmxvZygnW09wZnNTQUhQb29sXScsIC4uLmFyZ3MpO1xuICAgIH1cbiAgfVxuXG4gIHdhcm4oLi4uYXJncykge1xuICAgIGlmICh0aGlzLmxvZ0xldmVsID49IDIpIHsgIC8vIFdhcm5pbmcgbGV2ZWxcbiAgICAgIGNvbnNvbGUud2FybignW09wZnNTQUhQb29sXScsIC4uLmFyZ3MpO1xuICAgIH1cbiAgfVxuXG4gIGVycm9yKC4uLmFyZ3MpIHtcbiAgICBpZiAodGhpcy5sb2dMZXZlbCA+PSAxKSB7ICAvLyBFcnJvciBsZXZlbFxuICAgICAgY29uc29sZS5lcnJvcignW09wZnNTQUhQb29sXScsIC4uLmFyZ3MpO1xuICAgIH1cbiAgfVxuXG4gIGdldENhcGFjaXR5KCkge1xuICAgIHJldHVybiB0aGlzLiNtYXBTQUhUb05hbWUuc2l6ZTtcbiAgfVxuXG4gIGdldEZpbGVDb3VudCgpIHtcbiAgICByZXR1cm4gdGhpcy4jbWFwRmlsZW5hbWVUb1NBSC5zaXplO1xuICB9XG5cbiAgZ2V0RmlsZU5hbWVzKCkge1xuICAgIHJldHVybiBBcnJheS5mcm9tKHRoaXMuI21hcEZpbGVuYW1lVG9TQUgua2V5cygpKTtcbiAgfVxuXG4gIC8qKlxuICAgKiBBZGQgY2FwYWNpdHkgLSBjcmVhdGUgbiBuZXcgT1BGUyBmaWxlcyB3aXRoIHN5bmMgYWNjZXNzIGhhbmRsZXNcbiAgICovXG4gIGFzeW5jIGFkZENhcGFjaXR5KG4pIHtcbiAgICBmb3IgKGxldCBpID0gMDsgaSA8IG47ICsraSkge1xuICAgICAgY29uc3QgbmFtZSA9IGdldFJhbmRvbU5hbWUoKTtcbiAgICAgIGNvbnN0IGggPSBhd2FpdCB0aGlzLiNkaE9wYXF1ZS5nZXRGaWxlSGFuZGxlKG5hbWUsIHsgY3JlYXRlOiB0cnVlIH0pO1xuICAgICAgY29uc3QgYWggPSBhd2FpdCBoLmNyZWF0ZVN5bmNBY2Nlc3NIYW5kbGUoKTtcbiAgICAgIHRoaXMuI21hcFNBSFRvTmFtZS5zZXQoYWgsIG5hbWUpO1xuICAgICAgdGhpcy5zZXRBc3NvY2lhdGVkUGF0aChhaCwgJycsIDApO1xuICAgIH1cbiAgICB0aGlzLmxvZyhgQWRkZWQgJHtufSBoYW5kbGVzLCB0b3RhbCBjYXBhY2l0eTogJHt0aGlzLmdldENhcGFjaXR5KCl9YCk7XG4gICAgcmV0dXJuIHRoaXMuZ2V0Q2FwYWNpdHkoKTtcbiAgfVxuXG4gIC8qKlxuICAgKiBSZWxlYXNlIGFsbCBhY2Nlc3MgaGFuZGxlcyAoY2xlYW51cClcbiAgICovXG4gIHJlbGVhc2VBY2Nlc3NIYW5kbGVzKCkge1xuICAgIGZvciAoY29uc3QgYWggb2YgdGhpcy4jbWFwU0FIVG9OYW1lLmtleXMoKSkge1xuICAgICAgdHJ5IHtcbiAgICAgICAgYWguY2xvc2UoKTtcbiAgICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgICAgdGhpcy53YXJuKCdFcnJvciBjbG9zaW5nIGhhbmRsZTonLCBlKTtcbiAgICAgIH1cbiAgICB9XG4gICAgdGhpcy4jbWFwU0FIVG9OYW1lLmNsZWFyKCk7XG4gICAgdGhpcy4jbWFwRmlsZW5hbWVUb1NBSC5jbGVhcigpO1xuICAgIHRoaXMuI2F2YWlsYWJsZVNBSC5jbGVhcigpO1xuICAgIHRoaXMuI21hcEZpbGVJZFRvRmlsZS5jbGVhcigpO1xuICB9XG5cbiAgLyoqXG4gICAqIEFjcXVpcmUgYWxsIGV4aXN0aW5nIGFjY2VzcyBoYW5kbGVzIGZyb20gT1BGUyBkaXJlY3Rvcnkgd2l0aCByZXRyeSBsb2dpY1xuICAgKi9cbiAgYXN5bmMgYWNxdWlyZUFjY2Vzc0hhbmRsZXMoY2xlYXJGaWxlcyA9IGZhbHNlKSB7XG4gICAgY29uc3QgZmlsZXMgPSBbXTtcbiAgICBmb3IgYXdhaXQgKGNvbnN0IFtuYW1lLCBoXSBvZiB0aGlzLiNkaE9wYXF1ZSkge1xuICAgICAgaWYgKCdmaWxlJyA9PT0gaC5raW5kKSB7XG4gICAgICAgIGZpbGVzLnB1c2goW25hbWUsIGhdKTtcbiAgICAgIH1cbiAgICB9XG5cbiAgICAvLyBUcnkgdG8gYWNxdWlyZSBoYW5kbGVzIHdpdGggcmV0cmllcyB0byBhbGxvdyBHQyB0byByZWxlYXNlIG9sZCBoYW5kbGVzXG4gICAgY29uc3QgbWF4UmV0cmllcyA9IDM7XG4gICAgY29uc3QgcmV0cnlEZWxheSA9IDEwMDsgLy8gbXNcblxuICAgIGZvciAobGV0IGF0dGVtcHQgPSAwOyBhdHRlbXB0IDwgbWF4UmV0cmllczsgYXR0ZW1wdCsrKSB7XG4gICAgICBpZiAoYXR0ZW1wdCA+IDApIHtcbiAgICAgICAgdGhpcy53YXJuKGBSZXRyeSAke2F0dGVtcHR9LyR7bWF4UmV0cmllcyAtIDF9IGFmdGVyICR7cmV0cnlEZWxheX1tcyBkZWxheS4uLmApO1xuICAgICAgICBhd2FpdCBuZXcgUHJvbWlzZShyZXNvbHZlID0+IHNldFRpbWVvdXQocmVzb2x2ZSwgcmV0cnlEZWxheSAqIGF0dGVtcHQpKTtcbiAgICAgIH1cblxuICAgICAgY29uc3QgcmVzdWx0cyA9IGF3YWl0IFByb21pc2UuYWxsU2V0dGxlZChcbiAgICAgICAgZmlsZXMubWFwKGFzeW5jIChbbmFtZSwgaF0pID0+IHtcbiAgICAgICAgICB0cnkge1xuICAgICAgICAgICAgY29uc3QgYWggPSBhd2FpdCBoLmNyZWF0ZVN5bmNBY2Nlc3NIYW5kbGUoKTtcbiAgICAgICAgICAgIHRoaXMuI21hcFNBSFRvTmFtZS5zZXQoYWgsIG5hbWUpO1xuXG4gICAgICAgICAgICBpZiAoY2xlYXJGaWxlcykge1xuICAgICAgICAgICAgICBhaC50cnVuY2F0ZShIRUFERVJfT0ZGU0VUX0RBVEEpO1xuICAgICAgICAgICAgICB0aGlzLnNldEFzc29jaWF0ZWRQYXRoKGFoLCAnJywgMCk7XG4gICAgICAgICAgICB9IGVsc2Uge1xuICAgICAgICAgICAgICBjb25zdCBwYXRoID0gdGhpcy5nZXRBc3NvY2lhdGVkUGF0aChhaCk7XG4gICAgICAgICAgICAgIGlmIChwYXRoKSB7XG4gICAgICAgICAgICAgICAgdGhpcy4jbWFwRmlsZW5hbWVUb1NBSC5zZXQocGF0aCwgYWgpO1xuICAgICAgICAgICAgICAgIHRoaXMubG9nKGBSZXN0b3JlZCBmaWxlIGFzc29jaWF0aW9uOiAke3BhdGh9IC0+ICR7bmFtZX1gKTtcbiAgICAgICAgICAgICAgfSBlbHNlIHtcbiAgICAgICAgICAgICAgICB0aGlzLiNhdmFpbGFibGVTQUguYWRkKGFoKTtcbiAgICAgICAgICAgICAgfVxuICAgICAgICAgICAgfVxuICAgICAgICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgICAgICAgIGlmIChlLm5hbWUgPT09ICdOb01vZGlmaWNhdGlvbkFsbG93ZWRFcnJvcicpIHtcbiAgICAgICAgICAgICAgLy8gRmlsZSBpcyBsb2NrZWQgLSB3aWxsIHJldHJ5IG9yIGRlbGV0ZSBvbiBsYXN0IGF0dGVtcHRcbiAgICAgICAgICAgICAgdGhyb3cgZTtcbiAgICAgICAgICAgIH0gZWxzZSB7XG4gICAgICAgICAgICAgIHRoaXMuZXJyb3IoJ0Vycm9yIGFjcXVpcmluZyBoYW5kbGU6JywgZSk7XG4gICAgICAgICAgICAgIHRoaXMucmVsZWFzZUFjY2Vzc0hhbmRsZXMoKTtcbiAgICAgICAgICAgICAgdGhyb3cgZTtcbiAgICAgICAgICAgIH1cbiAgICAgICAgICB9XG4gICAgICAgIH0pXG4gICAgICApO1xuXG4gICAgICBjb25zdCBsb2NrZWQgPSByZXN1bHRzLmZpbHRlcihyID0+XG4gICAgICAgIHIuc3RhdHVzID09PSAncmVqZWN0ZWQnICYmXG4gICAgICAgIHIucmVhc29uPy5uYW1lID09PSAnTm9Nb2RpZmljYXRpb25BbGxvd2VkRXJyb3InXG4gICAgICApO1xuXG4gICAgICAvLyBJZiB3ZSBhY3F1aXJlZCBzb21lIGhhbmRsZXMgb3IgdGhpcyBpcyB0aGUgbGFzdCBhdHRlbXB0LCBkZWNpZGUgd2hhdCB0byBkb1xuICAgICAgaWYgKGxvY2tlZC5sZW5ndGggPT09IDAgfHwgYXR0ZW1wdCA9PT0gbWF4UmV0cmllcyAtIDEpIHtcbiAgICAgICAgaWYgKGxvY2tlZC5sZW5ndGggPiAwKSB7XG4gICAgICAgICAgLy8gTGFzdCBhdHRlbXB0IC0gZGVsZXRlIGxvY2tlZCBmaWxlcyBhcyBsYXN0IHJlc29ydFxuICAgICAgICAgIHRoaXMud2FybihgJHtsb2NrZWQubGVuZ3RofSBmaWxlcyBzdGlsbCBsb2NrZWQgYWZ0ZXIgJHttYXhSZXRyaWVzfSBhdHRlbXB0cywgZGVsZXRpbmcuLi5gKTtcbiAgICAgICAgICBmb3IgKGxldCBpID0gMDsgaSA8IGZpbGVzLmxlbmd0aDsgaSsrKSB7XG4gICAgICAgICAgICBpZiAocmVzdWx0c1tpXS5zdGF0dXMgPT09ICdyZWplY3RlZCcgJiYgcmVzdWx0c1tpXS5yZWFzb24/Lm5hbWUgPT09ICdOb01vZGlmaWNhdGlvbkFsbG93ZWRFcnJvcicpIHtcbiAgICAgICAgICAgICAgY29uc3QgW25hbWVdID0gZmlsZXNbaV07XG4gICAgICAgICAgICAgIHRyeSB7XG4gICAgICAgICAgICAgICAgYXdhaXQgdGhpcy4jZGhPcGFxdWUucmVtb3ZlRW50cnkobmFtZSk7XG4gICAgICAgICAgICAgICAgdGhpcy5sb2coYERlbGV0ZWQgbG9ja2VkIGZpbGU6ICR7bmFtZX1gKTtcbiAgICAgICAgICAgICAgfSBjYXRjaCAoZGVsZXRlRXJyb3IpIHtcbiAgICAgICAgICAgICAgICB0aGlzLndhcm4oYENvdWxkIG5vdCBkZWxldGUgbG9ja2VkIGZpbGU6ICR7bmFtZX1gLCBkZWxldGVFcnJvcik7XG4gICAgICAgICAgICAgIH1cbiAgICAgICAgICAgIH1cbiAgICAgICAgICB9XG4gICAgICAgIH1cblxuICAgICAgICAvLyBDaGVjayBpZiB3ZSBoYXZlIGFueSBjYXBhY2l0eSBhZnRlciBhbGwgYXR0ZW1wdHNcbiAgICAgICAgaWYgKHRoaXMuZ2V0Q2FwYWNpdHkoKSA9PT0gMCAmJiBmaWxlcy5sZW5ndGggPiAwKSB7XG4gICAgICAgICAgdGhyb3cgbmV3IEVycm9yKGBGYWlsZWQgdG8gYWNxdWlyZSBhbnkgYWNjZXNzIGhhbmRsZXMgZnJvbSAke2ZpbGVzLmxlbmd0aH0gZmlsZXNgKTtcbiAgICAgICAgfVxuXG4gICAgICAgIGJyZWFrOyAvLyBFeGl0IHJldHJ5IGxvb3BcbiAgICAgIH1cblxuICAgICAgLy8gQ2xlYXIgbWFwcyBmb3IgbmV4dCByZXRyeVxuICAgICAgdGhpcy4jbWFwU0FIVG9OYW1lLmNsZWFyKCk7XG4gICAgICB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILmNsZWFyKCk7XG4gICAgICB0aGlzLiNhdmFpbGFibGVTQUguY2xlYXIoKTtcbiAgICB9XG4gIH1cblxuICAvKipcbiAgICogR2V0IGFzc29jaWF0ZWQgcGF0aCBmcm9tIFNBSCBoZWFkZXJcbiAgICovXG4gIGdldEFzc29jaWF0ZWRQYXRoKHNhaCkge1xuICAgIHNhaC5yZWFkKHRoaXMuI2FwQm9keSwgeyBhdDogMCB9KTtcblxuICAgIGNvbnN0IGZsYWdzID0gdGhpcy4jZHZCb2R5LmdldFVpbnQzMihIRUFERVJfT0ZGU0VUX0ZMQUdTKTtcblxuICAgIC8vIENoZWNrIGlmIGZpbGUgc2hvdWxkIGJlIGRlbGV0ZWRcbiAgICBpZiAoXG4gICAgICB0aGlzLiNhcEJvZHlbMF0gJiZcbiAgICAgIChmbGFncyAmIFNRTElURV9PUEVOX0RFTEVURU9OQ0xPU0UgfHxcbiAgICAgICAgKGZsYWdzICYgUEVSU0lTVEVOVF9GSUxFX1RZUEVTKSA9PT0gMClcbiAgICApIHtcbiAgICAgIHRoaXMud2FybihgUmVtb3ZpbmcgZmlsZSB3aXRoIHVuZXhwZWN0ZWQgZmxhZ3MgJHtmbGFncy50b1N0cmluZygxNil9YCk7XG4gICAgICB0aGlzLnNldEFzc29jaWF0ZWRQYXRoKHNhaCwgJycsIDApO1xuICAgICAgcmV0dXJuICcnO1xuICAgIH1cblxuICAgIC8vIFZlcmlmeSBkaWdlc3RcbiAgICBjb25zdCBmaWxlRGlnZXN0ID0gbmV3IFVpbnQzMkFycmF5KEhFQURFUl9ESUdFU1RfU0laRSAvIDQpO1xuICAgIHNhaC5yZWFkKGZpbGVEaWdlc3QsIHsgYXQ6IEhFQURFUl9PRkZTRVRfRElHRVNUIH0pO1xuICAgIGNvbnN0IGNvbXBEaWdlc3QgPSB0aGlzLmNvbXB1dGVEaWdlc3QodGhpcy4jYXBCb2R5LCBmbGFncyk7XG5cbiAgICBpZiAoZmlsZURpZ2VzdC5ldmVyeSgodiwgaSkgPT4gdiA9PT0gY29tcERpZ2VzdFtpXSkpIHtcbiAgICAgIGNvbnN0IHBhdGhCeXRlcyA9IHRoaXMuI2FwQm9keS5maW5kSW5kZXgoKHYpID0+IDAgPT09IHYpO1xuICAgICAgaWYgKDAgPT09IHBhdGhCeXRlcykge1xuICAgICAgICBzYWgudHJ1bmNhdGUoSEVBREVSX09GRlNFVF9EQVRBKTtcbiAgICAgICAgcmV0dXJuICcnO1xuICAgICAgfVxuICAgICAgcmV0dXJuIHRleHREZWNvZGVyLmRlY29kZSh0aGlzLiNhcEJvZHkuc3ViYXJyYXkoMCwgcGF0aEJ5dGVzKSk7XG4gICAgfSBlbHNlIHtcbiAgICAgIHRoaXMud2FybignRGlzYXNzb2NpYXRpbmcgZmlsZSB3aXRoIGJhZCBkaWdlc3QnKTtcbiAgICAgIHRoaXMuc2V0QXNzb2NpYXRlZFBhdGgoc2FoLCAnJywgMCk7XG4gICAgICByZXR1cm4gJyc7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIFNldCBhc3NvY2lhdGVkIHBhdGggaW4gU0FIIGhlYWRlclxuICAgKi9cbiAgc2V0QXNzb2NpYXRlZFBhdGgoc2FoLCBwYXRoLCBmbGFncykge1xuICAgIGNvbnN0IGVuYyA9IHRleHRFbmNvZGVyLmVuY29kZUludG8ocGF0aCwgdGhpcy4jYXBCb2R5KTtcbiAgICBpZiAoSEVBREVSX01BWF9QQVRIX1NJWkUgPD0gZW5jLndyaXR0ZW4gKyAxKSB7XG4gICAgICB0aHJvdyBuZXcgRXJyb3IoYFBhdGggdG9vIGxvbmc6ICR7cGF0aH1gKTtcbiAgICB9XG5cbiAgICBpZiAocGF0aCAmJiBmbGFncykge1xuICAgICAgZmxhZ3MgfD0gRkxBR19DT01QVVRFX0RJR0VTVF9WMjtcbiAgICB9XG5cbiAgICB0aGlzLiNhcEJvZHkuZmlsbCgwLCBlbmMud3JpdHRlbiwgSEVBREVSX01BWF9QQVRIX1NJWkUpO1xuICAgIHRoaXMuI2R2Qm9keS5zZXRVaW50MzIoSEVBREVSX09GRlNFVF9GTEFHUywgZmxhZ3MpO1xuICAgIGNvbnN0IGRpZ2VzdCA9IHRoaXMuY29tcHV0ZURpZ2VzdCh0aGlzLiNhcEJvZHksIGZsYWdzKTtcblxuICAgIHNhaC53cml0ZSh0aGlzLiNhcEJvZHksIHsgYXQ6IDAgfSk7XG4gICAgc2FoLndyaXRlKGRpZ2VzdCwgeyBhdDogSEVBREVSX09GRlNFVF9ESUdFU1QgfSk7XG4gICAgc2FoLmZsdXNoKCk7XG5cbiAgICBpZiAocGF0aCkge1xuICAgICAgdGhpcy4jbWFwRmlsZW5hbWVUb1NBSC5zZXQocGF0aCwgc2FoKTtcbiAgICAgIHRoaXMuI2F2YWlsYWJsZVNBSC5kZWxldGUoc2FoKTtcbiAgICB9IGVsc2Uge1xuICAgICAgc2FoLnRydW5jYXRlKEhFQURFUl9PRkZTRVRfREFUQSk7XG4gICAgICB0aGlzLiNhdmFpbGFibGVTQUguYWRkKHNhaCk7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIENvbXB1dGUgZGlnZXN0IGZvciBmaWxlIGhlYWRlciAoY3lyYjUzLWluc3BpcmVkIGhhc2gpXG4gICAqL1xuICBjb21wdXRlRGlnZXN0KGJ5dGVBcnJheSwgZmlsZUZsYWdzKSB7XG4gICAgaWYgKGZpbGVGbGFncyAmIEZMQUdfQ09NUFVURV9ESUdFU1RfVjIpIHtcbiAgICAgIGxldCBoMSA9IDB4ZGVhZGJlZWY7XG4gICAgICBsZXQgaDIgPSAweDQxYzZjZTU3O1xuICAgICAgZm9yIChjb25zdCB2IG9mIGJ5dGVBcnJheSkge1xuICAgICAgICBoMSA9IE1hdGguaW11bChoMSBeIHYsIDI2NTQ0MzU3NjEpO1xuICAgICAgICBoMiA9IE1hdGguaW11bChoMiBeIHYsIDEwNDcyOSk7XG4gICAgICB9XG4gICAgICByZXR1cm4gbmV3IFVpbnQzMkFycmF5KFtoMSA+Pj4gMCwgaDIgPj4+IDBdKTtcbiAgICB9IGVsc2Uge1xuICAgICAgcmV0dXJuIG5ldyBVaW50MzJBcnJheShbMCwgMF0pO1xuICAgIH1cbiAgfVxuXG4gIC8qKlxuICAgKiBSZXNldC9pbml0aWFsaXplIHRoZSBwb29sXG4gICAqL1xuICBhc3luYyByZXNldChjbGVhckZpbGVzKSB7XG4gICAgbGV0IGggPSBhd2FpdCBuYXZpZ2F0b3Iuc3RvcmFnZS5nZXREaXJlY3RvcnkoKTtcbiAgICBsZXQgcHJldjtcblxuICAgIGZvciAoY29uc3QgZCBvZiB0aGlzLnZmc0Rpci5zcGxpdCgnLycpKSB7XG4gICAgICBpZiAoZCkge1xuICAgICAgICBwcmV2ID0gaDtcbiAgICAgICAgaCA9IGF3YWl0IGguZ2V0RGlyZWN0b3J5SGFuZGxlKGQsIHsgY3JlYXRlOiB0cnVlIH0pO1xuICAgICAgfVxuICAgIH1cblxuICAgIHRoaXMuI2RoVmZzUm9vdCA9IGg7XG4gICAgdGhpcy4jZGhWZnNQYXJlbnQgPSBwcmV2O1xuICAgIHRoaXMuI2RoT3BhcXVlID0gYXdhaXQgdGhpcy4jZGhWZnNSb290LmdldERpcmVjdG9yeUhhbmRsZShPUEFRVUVfRElSX05BTUUsIHtcbiAgICAgIGNyZWF0ZTogdHJ1ZSxcbiAgICB9KTtcblxuICAgIHRoaXMucmVsZWFzZUFjY2Vzc0hhbmRsZXMoKTtcbiAgICByZXR1cm4gdGhpcy5hY3F1aXJlQWNjZXNzSGFuZGxlcyhjbGVhckZpbGVzKTtcbiAgfVxuXG4gIC8qKlxuICAgKiBHZXQgcGF0aCAoaGFuZGxlIGJvdGggc3RyaW5nIGFuZCBwb2ludGVyKVxuICAgKi9cbiAgZ2V0UGF0aChhcmcpIHtcbiAgICBpZiAodHlwZW9mIGFyZyA9PT0gJ3N0cmluZycpIHtcbiAgICAgIHJldHVybiBuZXcgVVJMKGFyZywgJ2ZpbGU6Ly9sb2NhbGhvc3QvJykucGF0aG5hbWU7XG4gICAgfVxuICAgIHJldHVybiBhcmc7XG4gIH1cblxuICAvKipcbiAgICogQ2hlY2sgaWYgZmlsZW5hbWUgZXhpc3RzXG4gICAqL1xuICBoYXNGaWxlbmFtZShuYW1lKSB7XG4gICAgcmV0dXJuIHRoaXMuI21hcEZpbGVuYW1lVG9TQUguaGFzKG5hbWUpO1xuICB9XG5cbiAgLyoqXG4gICAqIEdldCBTQUggZm9yIHBhdGhcbiAgICovXG4gIGdldFNBSEZvclBhdGgocGF0aCkge1xuICAgIHJldHVybiB0aGlzLiNtYXBGaWxlbmFtZVRvU0FILmdldChwYXRoKTtcbiAgfVxuXG4gIC8qKlxuICAgKiBHZXQgbmV4dCBhdmFpbGFibGUgU0FIXG4gICAqL1xuICBuZXh0QXZhaWxhYmxlU0FIKCkge1xuICAgIGNvbnN0IFtyY10gPSB0aGlzLiNhdmFpbGFibGVTQUgua2V5cygpO1xuICAgIHJldHVybiByYztcbiAgfVxuXG4gIC8qKlxuICAgKiBEZWxldGUgcGF0aCBhc3NvY2lhdGlvblxuICAgKi9cbiAgZGVsZXRlUGF0aChwYXRoKSB7XG4gICAgY29uc3Qgc2FoID0gdGhpcy4jbWFwRmlsZW5hbWVUb1NBSC5nZXQocGF0aCk7XG4gICAgaWYgKHNhaCkge1xuICAgICAgdGhpcy4jbWFwRmlsZW5hbWVUb1NBSC5kZWxldGUocGF0aCk7XG4gICAgICB0aGlzLnNldEFzc29jaWF0ZWRQYXRoKHNhaCwgJycsIDApO1xuICAgICAgcmV0dXJuIHRydWU7XG4gICAgfVxuICAgIHJldHVybiBmYWxzZTtcbiAgfVxuXG4gIC8vID09PT09IFZGUyBNZXRob2RzIChjYWxsZWQgZnJvbSBFTV9KUyBob29rcykgPT09PT1cblxuICAvKipcbiAgICogT3BlbiBhIGZpbGUgLSByZXR1cm5zIGZpbGUgSURcbiAgICovXG4gIHhPcGVuKGZpbGVuYW1lLCBmbGFncykge1xuICAgIHRyeSB7XG4gICAgICBjb25zdCBwYXRoID0gdGhpcy5nZXRQYXRoKGZpbGVuYW1lKTtcbiAgICAgIHRoaXMubG9nKGB4T3BlbjogJHtwYXRofSBmbGFncz0ke2ZsYWdzfWApO1xuXG4gICAgICBsZXQgc2FoID0gdGhpcy5nZXRTQUhGb3JQYXRoKHBhdGgpO1xuXG4gICAgICBpZiAoIXNhaCAmJiAoZmxhZ3MgJiBTUUxJVEVfT1BFTl9DUkVBVEUpKSB7XG4gICAgICAgIGlmICh0aGlzLmdldEZpbGVDb3VudCgpIDwgdGhpcy5nZXRDYXBhY2l0eSgpKSB7XG4gICAgICAgICAgc2FoID0gdGhpcy5uZXh0QXZhaWxhYmxlU0FIKCk7XG4gICAgICAgICAgaWYgKHNhaCkge1xuICAgICAgICAgICAgdGhpcy5zZXRBc3NvY2lhdGVkUGF0aChzYWgsIHBhdGgsIGZsYWdzKTtcbiAgICAgICAgICB9IGVsc2Uge1xuICAgICAgICAgICAgdGhpcy5lcnJvcignTm8gYXZhaWxhYmxlIFNBSCBpbiBwb29sJyk7XG4gICAgICAgICAgICByZXR1cm4gLTE7XG4gICAgICAgICAgfVxuICAgICAgICB9IGVsc2Uge1xuICAgICAgICAgIHRoaXMuZXJyb3IoJ1NBSCBwb29sIGlzIGZ1bGwsIGNhbm5vdCBjcmVhdGUgZmlsZScpO1xuICAgICAgICAgIHJldHVybiAtMTtcbiAgICAgICAgfVxuICAgICAgfVxuXG4gICAgICBpZiAoIXNhaCkge1xuICAgICAgICAvLyBPbmx5IGxvZyBhcyBlcnJvciBpZiBDUkVBVEUgZmxhZyB3YXMgc2V0ICh1bmV4cGVjdGVkIGZhaWx1cmUpXG4gICAgICAgIC8vIEZvciBSRUFET05MWSBvcGVucywgZmlsZSBub3QgZm91bmQgaXMgbm9ybWFsIG9uIGZpcnN0IHJ1blxuICAgICAgICBpZiAoZmxhZ3MgJiBTUUxJVEVfT1BFTl9DUkVBVEUpIHtcbiAgICAgICAgICB0aGlzLmVycm9yKGBGaWxlIG5vdCBmb3VuZDogJHtwYXRofWApO1xuICAgICAgICB9IGVsc2Uge1xuICAgICAgICAgIHRoaXMubG9nKGBGaWxlIG5vdCBmb3VuZCAoUkVBRE9OTFkgb3Blbik6ICR7cGF0aH1gKTtcbiAgICAgICAgfVxuICAgICAgICByZXR1cm4gLTE7XG4gICAgICB9XG5cbiAgICAgIC8vIEFsbG9jYXRlIGZpbGUgSURcbiAgICAgIGNvbnN0IGZpbGVJZCA9IHRoaXMuI25leHRGaWxlSWQrKztcbiAgICAgIHRoaXMuI21hcEZpbGVJZFRvRmlsZS5zZXQoZmlsZUlkLCB7XG4gICAgICAgIHBhdGgsXG4gICAgICAgIHNhaCxcbiAgICAgICAgZmxhZ3MsXG4gICAgICAgIGxvY2tUeXBlOiBTUUxJVEVfTE9DS19OT05FXG4gICAgICB9KTtcblxuICAgICAgdGhpcy5sb2coYHhPcGVuIHN1Y2Nlc3M6ICR7cGF0aH0gLT4gZmlsZUlkICR7ZmlsZUlkfWApO1xuICAgICAgcmV0dXJuIGZpbGVJZDtcblxuICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgIHRoaXMuZXJyb3IoJ3hPcGVuIGVycm9yOicsIGUpO1xuICAgICAgcmV0dXJuIC0xO1xuICAgIH1cbiAgfVxuXG4gIC8qKlxuICAgKiBSZWFkIGZyb20gZmlsZVxuICAgKi9cbiAgeFJlYWQoZmlsZUlkLCBidWZmZXIsIGFtb3VudCwgb2Zmc2V0KSB7XG4gICAgdHJ5IHtcbiAgICAgIGNvbnN0IGZpbGUgPSB0aGlzLiNtYXBGaWxlSWRUb0ZpbGUuZ2V0KGZpbGVJZCk7XG4gICAgICBpZiAoIWZpbGUpIHtcbiAgICAgICAgdGhpcy5lcnJvcihgeFJlYWQ6IGludmFsaWQgZmlsZUlkICR7ZmlsZUlkfWApO1xuICAgICAgICByZXR1cm4gU1FMSVRFX0lPRVJSX1JFQUQ7XG4gICAgICB9XG5cbiAgICAgIGNvbnN0IG5SZWFkID0gZmlsZS5zYWgucmVhZChidWZmZXIsIHsgYXQ6IEhFQURFUl9PRkZTRVRfREFUQSArIG9mZnNldCB9KTtcblxuICAgICAgaWYgKG5SZWFkIDwgYW1vdW50KSB7XG4gICAgICAgIC8vIFNob3J0IHJlYWQgLSBmaWxsIHJlc3Qgd2l0aCB6ZXJvc1xuICAgICAgICBidWZmZXIuZmlsbCgwLCBuUmVhZCk7XG4gICAgICAgIHJldHVybiBTUUxJVEVfSU9FUlJfU0hPUlRfUkVBRDtcbiAgICAgIH1cblxuICAgICAgcmV0dXJuIFNRTElURV9PSztcblxuICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgIHRoaXMuZXJyb3IoJ3hSZWFkIGVycm9yOicsIGUpO1xuICAgICAgcmV0dXJuIFNRTElURV9JT0VSUl9SRUFEO1xuICAgIH1cbiAgfVxuXG4gIC8qKlxuICAgKiBXcml0ZSB0byBmaWxlXG4gICAqL1xuICB4V3JpdGUoZmlsZUlkLCBidWZmZXIsIGFtb3VudCwgb2Zmc2V0KSB7XG4gICAgdHJ5IHtcbiAgICAgIGNvbnN0IGZpbGUgPSB0aGlzLiNtYXBGaWxlSWRUb0ZpbGUuZ2V0KGZpbGVJZCk7XG4gICAgICBpZiAoIWZpbGUpIHtcbiAgICAgICAgdGhpcy5lcnJvcihgeFdyaXRlOiBpbnZhbGlkIGZpbGVJZCAke2ZpbGVJZH1gKTtcbiAgICAgICAgcmV0dXJuIFNRTElURV9JT0VSUl9XUklURTtcbiAgICAgIH1cblxuICAgICAgY29uc3QgbldyaXR0ZW4gPSBmaWxlLnNhaC53cml0ZShidWZmZXIsIHsgYXQ6IEhFQURFUl9PRkZTRVRfREFUQSArIG9mZnNldCB9KTtcblxuICAgICAgaWYgKG5Xcml0dGVuICE9PSBhbW91bnQpIHtcbiAgICAgICAgdGhpcy5lcnJvcihgeFdyaXRlOiB3cm90ZSAke25Xcml0dGVufS8ke2Ftb3VudH0gYnl0ZXNgKTtcbiAgICAgICAgcmV0dXJuIFNRTElURV9JT0VSUl9XUklURTtcbiAgICAgIH1cblxuICAgICAgcmV0dXJuIFNRTElURV9PSztcblxuICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgIHRoaXMuZXJyb3IoJ3hXcml0ZSBlcnJvcjonLCBlKTtcbiAgICAgIHJldHVybiBTUUxJVEVfSU9FUlJfV1JJVEU7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIFN5bmMgZmlsZSB0byBzdG9yYWdlXG4gICAqL1xuICB4U3luYyhmaWxlSWQsIGZsYWdzKSB7XG4gICAgdHJ5IHtcbiAgICAgIGNvbnN0IGZpbGUgPSB0aGlzLiNtYXBGaWxlSWRUb0ZpbGUuZ2V0KGZpbGVJZCk7XG4gICAgICBpZiAoIWZpbGUpIHtcbiAgICAgICAgcmV0dXJuIFNRTElURV9JT0VSUjtcbiAgICAgIH1cblxuICAgICAgZmlsZS5zYWguZmx1c2goKTtcbiAgICAgIHJldHVybiBTUUxJVEVfT0s7XG5cbiAgICB9IGNhdGNoIChlKSB7XG4gICAgICB0aGlzLmVycm9yKCd4U3luYyBlcnJvcjonLCBlKTtcbiAgICAgIHJldHVybiBTUUxJVEVfSU9FUlI7XG4gICAgfVxuICB9XG5cbiAgLyoqXG4gICAqIFRydW5jYXRlIGZpbGVcbiAgICovXG4gIHhUcnVuY2F0ZShmaWxlSWQsIHNpemUpIHtcbiAgICB0cnkge1xuICAgICAgY29uc3QgZmlsZSA9IHRoaXMuI21hcEZpbGVJZFRvRmlsZS5nZXQoZmlsZUlkKTtcbiAgICAgIGlmICghZmlsZSkge1xuICAgICAgICByZXR1cm4gU1FMSVRFX0lPRVJSO1xuICAgICAgfVxuXG4gICAgICBmaWxlLnNhaC50cnVuY2F0ZShIRUFERVJfT0ZGU0VUX0RBVEEgKyBzaXplKTtcbiAgICAgIHJldHVybiBTUUxJVEVfT0s7XG5cbiAgICB9IGNhdGNoIChlKSB7XG4gICAgICB0aGlzLmVycm9yKCd4VHJ1bmNhdGUgZXJyb3I6JywgZSk7XG4gICAgICByZXR1cm4gU1FMSVRFX0lPRVJSO1xuICAgIH1cbiAgfVxuXG4gIC8qKlxuICAgKiBHZXQgZmlsZSBzaXplXG4gICAqL1xuICB4RmlsZVNpemUoZmlsZUlkKSB7XG4gICAgdHJ5IHtcbiAgICAgIGNvbnN0IGZpbGUgPSB0aGlzLiNtYXBGaWxlSWRUb0ZpbGUuZ2V0KGZpbGVJZCk7XG4gICAgICBpZiAoIWZpbGUpIHtcbiAgICAgICAgcmV0dXJuIC0xO1xuICAgICAgfVxuXG4gICAgICBjb25zdCBzaXplID0gZmlsZS5zYWguZ2V0U2l6ZSgpIC0gSEVBREVSX09GRlNFVF9EQVRBO1xuICAgICAgcmV0dXJuIE1hdGgubWF4KDAsIHNpemUpO1xuXG4gICAgfSBjYXRjaCAoZSkge1xuICAgICAgdGhpcy5lcnJvcigneEZpbGVTaXplIGVycm9yOicsIGUpO1xuICAgICAgcmV0dXJuIC0xO1xuICAgIH1cbiAgfVxuXG4gIC8qKlxuICAgKiBDbG9zZSBmaWxlXG4gICAqL1xuICB4Q2xvc2UoZmlsZUlkKSB7XG4gICAgdHJ5IHtcbiAgICAgIGNvbnN0IGZpbGUgPSB0aGlzLiNtYXBGaWxlSWRUb0ZpbGUuZ2V0KGZpbGVJZCk7XG4gICAgICBpZiAoIWZpbGUpIHtcbiAgICAgICAgcmV0dXJuIFNRTElURV9FUlJPUjtcbiAgICAgIH1cblxuICAgICAgLy8gRG9uJ3QgY2xvc2UgdGhlIFNBSCAtIGl0J3MgcmV1c2VkIGluIHRoZSBwb29sXG4gICAgICAvLyBKdXN0IHJlbW92ZSBmcm9tIG9wZW4gZmlsZXMgbWFwXG4gICAgICB0aGlzLiNtYXBGaWxlSWRUb0ZpbGUuZGVsZXRlKGZpbGVJZCk7XG5cbiAgICAgIHRoaXMubG9nKGB4Q2xvc2U6IGZpbGVJZCAke2ZpbGVJZH0gKCR7ZmlsZS5wYXRofSlgKTtcbiAgICAgIHJldHVybiBTUUxJVEVfT0s7XG5cbiAgICB9IGNhdGNoIChlKSB7XG4gICAgICB0aGlzLmVycm9yKCd4Q2xvc2UgZXJyb3I6JywgZSk7XG4gICAgICByZXR1cm4gU1FMSVRFX0VSUk9SO1xuICAgIH1cbiAgfVxuXG4gIC8qKlxuICAgKiBDaGVjayBmaWxlIGFjY2Vzc1xuICAgKi9cbiAgeEFjY2VzcyhmaWxlbmFtZSwgZmxhZ3MpIHtcbiAgICB0cnkge1xuICAgICAgY29uc3QgcGF0aCA9IHRoaXMuZ2V0UGF0aChmaWxlbmFtZSk7XG4gICAgICByZXR1cm4gdGhpcy5oYXNGaWxlbmFtZShwYXRoKSA/IDEgOiAwO1xuICAgIH0gY2F0Y2ggKGUpIHtcbiAgICAgIHRoaXMuZXJyb3IoJ3hBY2Nlc3MgZXJyb3I6JywgZSk7XG4gICAgICByZXR1cm4gMDtcbiAgICB9XG4gIH1cblxuICAvKipcbiAgICogRGVsZXRlIGZpbGVcbiAgICovXG4gIHhEZWxldGUoZmlsZW5hbWUsIHN5bmNEaXIpIHtcbiAgICB0cnkge1xuICAgICAgY29uc3QgcGF0aCA9IHRoaXMuZ2V0UGF0aChmaWxlbmFtZSk7XG4gICAgICB0aGlzLmxvZyhgeERlbGV0ZTogJHtwYXRofWApO1xuICAgICAgdGhpcy5kZWxldGVQYXRoKHBhdGgpO1xuICAgICAgcmV0dXJuIFNRTElURV9PSztcbiAgICB9IGNhdGNoIChlKSB7XG4gICAgICB0aGlzLmVycm9yKCd4RGVsZXRlIGVycm9yOicsIGUpO1xuICAgICAgcmV0dXJuIFNRTElURV9JT0VSUjtcbiAgICB9XG4gIH1cbn1cblxuLy8gRXhwb3J0IHNpbmdsZXRvbiBpbnN0YW5jZVxuY29uc3Qgb3Bmc1NBSFBvb2wgPSBuZXcgT3Bmc1NBSFBvb2woe1xuICBkaXJlY3Rvcnk6ICcub3Bmcy1zYWhwb29sJyxcbiAgaW5pdGlhbENhcGFjaXR5OiA2LFxuICBjbGVhck9uSW5pdDogZmFsc2Vcbn0pO1xuXG4vLyBNYWtlIGF2YWlsYWJsZSBnbG9iYWxseSBmb3IgRU1fSlMgaG9va3NcbmlmICh0eXBlb2YgZ2xvYmFsVGhpcyAhPT0gJ3VuZGVmaW5lZCcpIHtcbiAgZ2xvYmFsVGhpcy5vcGZzU0FIUG9vbCA9IG9wZnNTQUhQb29sO1xufVxuXG4vLyBFUzYgbW9kdWxlIGV4cG9ydFxuZXhwb3J0IHsgT3Bmc1NBSFBvb2wsIG9wZnNTQUhQb29sIH07XG4iLCAiLy8gb3Bmcy13b3JrZXIudHNcbi8vIFdlYiBXb3JrZXIgZm9yIE9QRlMgZmlsZSBJL08gdXNpbmcgU0FIUG9vbFxuLy8gSGFuZGxlcyBvbmx5IGZpbGUgcmVhZC93cml0ZSBvcGVyYXRpb25zIC0gbm8gU1FMIGV4ZWN1dGlvblxuXG5pbXBvcnQgeyBvcGZzU0FIUG9vbCB9IGZyb20gJy4vb3Bmcy1zYWhwb29sJztcblxuaW50ZXJmYWNlIFdvcmtlck1lc3NhZ2Uge1xuICAgIGlkOiBudW1iZXI7XG4gICAgdHlwZTogc3RyaW5nO1xuICAgIGFyZ3M/OiBhbnk7XG59XG5cbmludGVyZmFjZSBXb3JrZXJSZXNwb25zZSB7XG4gICAgaWQ6IG51bWJlcjtcbiAgICBzdWNjZXNzOiBib29sZWFuO1xuICAgIHJlc3VsdD86IGFueTtcbiAgICBlcnJvcj86IHN0cmluZztcbn1cblxuLy8gU2ltcGxlIGxvZ2dlciBmb3Igd29ya2VyIGNvbnRleHQgKG1hdGNoZXMgT3Bmc0xvZ0xldmVsIGVudW0pXG5lbnVtIExvZ0xldmVsIHsgTm9uZSA9IDAsIEVycm9yID0gMSwgV2FybmluZyA9IDIsIEluZm8gPSAzLCBEZWJ1ZyA9IDQgfVxubGV0IHdvcmtlckxvZ0xldmVsID0gTG9nTGV2ZWwuV2FybmluZzsgLy8gRGVmYXVsdFxuXG5jb25zdCBsb2cgPSB7XG4gICAgZGVidWc6ICguLi5hcmdzOiBhbnlbXSkgPT4gd29ya2VyTG9nTGV2ZWwgPj0gTG9nTGV2ZWwuRGVidWcgJiYgY29uc29sZS5sb2coJ1tPUEZTIFdvcmtlcl0nLCAuLi5hcmdzKSxcbiAgICBpbmZvOiAoLi4uYXJnczogYW55W10pID0+IHdvcmtlckxvZ0xldmVsID49IExvZ0xldmVsLkluZm8gJiYgY29uc29sZS5sb2coJ1tPUEZTIFdvcmtlcl0gXHUyNzEzJywgLi4uYXJncyksXG4gICAgd2FybjogKC4uLmFyZ3M6IGFueVtdKSA9PiB3b3JrZXJMb2dMZXZlbCA+PSBMb2dMZXZlbC5XYXJuaW5nICYmIGNvbnNvbGUud2FybignW09QRlMgV29ya2VyXSBcdTI2QTAnLCAuLi5hcmdzKSxcbiAgICBlcnJvcjogKC4uLmFyZ3M6IGFueVtdKSA9PiB3b3JrZXJMb2dMZXZlbCA+PSBMb2dMZXZlbC5FcnJvciAmJiBjb25zb2xlLmVycm9yKCdbT1BGUyBXb3JrZXJdIFx1Mjc0QycsIC4uLmFyZ3MpXG59O1xuXG4vLyBXYWl0IGZvciBPUEZTIFNBSFBvb2wgdG8gaW5pdGlhbGl6ZVxub3Bmc1NBSFBvb2wuaXNSZWFkeS50aGVuKCgpID0+IHtcbiAgICBsb2cuaW5mbygnU0FIUG9vbCBpbml0aWFsaXplZCwgc2VuZGluZyByZWFkeSBzaWduYWwnKTtcbiAgICBzZWxmLnBvc3RNZXNzYWdlKHsgdHlwZTogJ3JlYWR5JyB9KTtcbn0pLmNhdGNoKChlcnJvcikgPT4ge1xuICAgIGxvZy5lcnJvcignU0FIUG9vbCBpbml0aWFsaXphdGlvbiBmYWlsZWQ6JywgZXJyb3IpO1xuICAgIHNlbGYucG9zdE1lc3NhZ2UoeyB0eXBlOiAnZXJyb3InLCBlcnJvcjogZXJyb3IubWVzc2FnZSB9KTtcbn0pO1xuXG4vLyBIYW5kbGUgbWVzc2FnZXMgZnJvbSBtYWluIHRocmVhZFxuc2VsZi5vbm1lc3NhZ2UgPSBhc3luYyAoZXZlbnQ6IE1lc3NhZ2VFdmVudDxXb3JrZXJNZXNzYWdlPikgPT4ge1xuICAgIGNvbnN0IHsgaWQsIHR5cGUsIGFyZ3MgfSA9IGV2ZW50LmRhdGE7XG5cbiAgICB0cnkge1xuICAgICAgICBsZXQgcmVzdWx0OiBhbnk7XG5cbiAgICAgICAgc3dpdGNoICh0eXBlKSB7XG4gICAgICAgICAgICBjYXNlICdzZXRMb2dMZXZlbCc6XG4gICAgICAgICAgICAgICAgLy8gQ29uZmlndXJlIGxvZyBsZXZlbCBmb3Igd29ya2VyIGFuZCBTQUhQb29sXG4gICAgICAgICAgICAgICAgd29ya2VyTG9nTGV2ZWwgPSBhcmdzLmxldmVsO1xuICAgICAgICAgICAgICAgIG9wZnNTQUhQb29sLmxvZ0xldmVsID0gYXJncy5sZXZlbDtcbiAgICAgICAgICAgICAgICBsb2cuaW5mbyhgTG9nIGxldmVsIHNldCB0byAke2FyZ3MubGV2ZWx9YCk7XG4gICAgICAgICAgICAgICAgcmVzdWx0ID0geyBzdWNjZXNzOiB0cnVlIH07XG4gICAgICAgICAgICAgICAgYnJlYWs7XG5cbiAgICAgICAgICAgIGNhc2UgJ2NsZWFudXAnOlxuICAgICAgICAgICAgICAgIC8vIFJlbGVhc2UgYWxsIE9QRlMgaGFuZGxlcyBiZWZvcmUgcGFnZSB1bmxvYWRcbiAgICAgICAgICAgICAgICBsb2cuaW5mbygnQ2xlYW5pbmcgdXAgaGFuZGxlcyBiZWZvcmUgdW5sb2FkLi4uJyk7XG4gICAgICAgICAgICAgICAgb3Bmc1NBSFBvb2wucmVsZWFzZUFjY2Vzc0hhbmRsZXMoKTtcbiAgICAgICAgICAgICAgICBsb2cuaW5mbygnQ2xlYW51cCBjb21wbGV0ZScpO1xuICAgICAgICAgICAgICAgIHJlc3VsdCA9IHsgc3VjY2VzczogdHJ1ZSB9O1xuICAgICAgICAgICAgICAgIGJyZWFrO1xuXG4gICAgICAgICAgICBjYXNlICdnZXRDYXBhY2l0eSc6XG4gICAgICAgICAgICAgICAgcmVzdWx0ID0ge1xuICAgICAgICAgICAgICAgICAgICBjYXBhY2l0eTogb3Bmc1NBSFBvb2wuZ2V0Q2FwYWNpdHkoKVxuICAgICAgICAgICAgICAgIH07XG4gICAgICAgICAgICAgICAgYnJlYWs7XG5cbiAgICAgICAgICAgIGNhc2UgJ2FkZENhcGFjaXR5JzpcbiAgICAgICAgICAgICAgICByZXN1bHQgPSB7XG4gICAgICAgICAgICAgICAgICAgIG5ld0NhcGFjaXR5OiBhd2FpdCBvcGZzU0FIUG9vbC5hZGRDYXBhY2l0eShhcmdzLmNvdW50KVxuICAgICAgICAgICAgICAgIH07XG4gICAgICAgICAgICAgICAgYnJlYWs7XG5cbiAgICAgICAgICAgIGNhc2UgJ2dldEZpbGVMaXN0JzpcbiAgICAgICAgICAgICAgICByZXN1bHQgPSB7XG4gICAgICAgICAgICAgICAgICAgIGZpbGVzOiBvcGZzU0FIUG9vbC5nZXRGaWxlTmFtZXMoKVxuICAgICAgICAgICAgICAgIH07XG4gICAgICAgICAgICAgICAgYnJlYWs7XG5cbiAgICAgICAgICAgIGNhc2UgJ3JlYWRGaWxlJzpcbiAgICAgICAgICAgICAgICAvLyBSZWFkIGZpbGUgZnJvbSBPUEZTIHVzaW5nIFNBSFBvb2xcbiAgICAgICAgICAgICAgICBjb25zdCBmaWxlSWQgPSBvcGZzU0FIUG9vbC54T3BlbihhcmdzLmZpbGVuYW1lLCAweDAxKTsgLy8gUkVBRE9OTFlcbiAgICAgICAgICAgICAgICBpZiAoZmlsZUlkIDwgMCkge1xuICAgICAgICAgICAgICAgICAgICAvLyBGaWxlIGRvZXNuJ3QgZXhpc3QgeWV0IC0gdGhpcyBpcyBub3JtYWwgb24gZmlyc3QgcnVuXG4gICAgICAgICAgICAgICAgICAgIGxvZy5kZWJ1ZyhgRmlsZSBub3QgZm91bmQgaW4gT1BGUzogJHthcmdzLmZpbGVuYW1lfSAod2lsbCBiZSBjcmVhdGVkIG9uIGZpcnN0IHdyaXRlKWApO1xuICAgICAgICAgICAgICAgICAgICByZXN1bHQgPSB7XG4gICAgICAgICAgICAgICAgICAgICAgICBkYXRhOiBbXSAvLyBSZXR1cm4gZW1wdHkgYXJyYXkgdG8gaW5kaWNhdGUgZmlsZSBkb2Vzbid0IGV4aXN0XG4gICAgICAgICAgICAgICAgICAgIH07XG4gICAgICAgICAgICAgICAgICAgIGJyZWFrO1xuICAgICAgICAgICAgICAgIH1cblxuICAgICAgICAgICAgICAgIGNvbnN0IHNpemUgPSBvcGZzU0FIUG9vbC54RmlsZVNpemUoZmlsZUlkKTtcbiAgICAgICAgICAgICAgICBjb25zdCBidWZmZXIgPSBuZXcgVWludDhBcnJheShzaXplKTtcbiAgICAgICAgICAgICAgICBjb25zdCByZWFkUmVzdWx0ID0gb3Bmc1NBSFBvb2wueFJlYWQoZmlsZUlkLCBidWZmZXIsIHNpemUsIDApO1xuICAgICAgICAgICAgICAgIG9wZnNTQUhQb29sLnhDbG9zZShmaWxlSWQpO1xuXG4gICAgICAgICAgICAgICAgaWYgKHJlYWRSZXN1bHQgIT09IDApIHtcbiAgICAgICAgICAgICAgICAgICAgdGhyb3cgbmV3IEVycm9yKGBGYWlsZWQgdG8gcmVhZCBmaWxlOiAke2FyZ3MuZmlsZW5hbWV9YCk7XG4gICAgICAgICAgICAgICAgfVxuXG4gICAgICAgICAgICAgICAgcmVzdWx0ID0ge1xuICAgICAgICAgICAgICAgICAgICBkYXRhOiBBcnJheS5mcm9tKGJ1ZmZlcilcbiAgICAgICAgICAgICAgICB9O1xuICAgICAgICAgICAgICAgIGJyZWFrO1xuXG4gICAgICAgICAgICBjYXNlICd3cml0ZUZpbGUnOlxuICAgICAgICAgICAgICAgIC8vIFdyaXRlIGZpbGUgdG8gT1BGUyB1c2luZyBTQUhQb29sXG4gICAgICAgICAgICAgICAgY29uc3QgZGF0YSA9IG5ldyBVaW50OEFycmF5KGFyZ3MuZGF0YSk7XG4gICAgICAgICAgICAgICAgY29uc3Qgd3JpdGVGaWxlSWQgPSBvcGZzU0FIUG9vbC54T3BlbihcbiAgICAgICAgICAgICAgICAgICAgYXJncy5maWxlbmFtZSxcbiAgICAgICAgICAgICAgICAgICAgMHgwMiB8IDB4MDQgfCAweDEwMCAvLyBSRUFEV1JJVEUgfCBDUkVBVEUgfCBNQUlOX0RCXG4gICAgICAgICAgICAgICAgKTtcblxuICAgICAgICAgICAgICAgIGlmICh3cml0ZUZpbGVJZCA8IDApIHtcbiAgICAgICAgICAgICAgICAgICAgdGhyb3cgbmV3IEVycm9yKGBGYWlsZWQgdG8gb3BlbiBmaWxlIGZvciB3cml0aW5nOiAke2FyZ3MuZmlsZW5hbWV9YCk7XG4gICAgICAgICAgICAgICAgfVxuXG4gICAgICAgICAgICAgICAgLy8gVHJ1bmNhdGUgdG8gZXhhY3Qgc2l6ZVxuICAgICAgICAgICAgICAgIG9wZnNTQUhQb29sLnhUcnVuY2F0ZSh3cml0ZUZpbGVJZCwgZGF0YS5sZW5ndGgpO1xuXG4gICAgICAgICAgICAgICAgLy8gV3JpdGUgZGF0YVxuICAgICAgICAgICAgICAgIGNvbnN0IHdyaXRlUmVzdWx0ID0gb3Bmc1NBSFBvb2wueFdyaXRlKHdyaXRlRmlsZUlkLCBkYXRhLCBkYXRhLmxlbmd0aCwgMCk7XG5cbiAgICAgICAgICAgICAgICAvLyBTeW5jIHRvIGRpc2tcbiAgICAgICAgICAgICAgICBvcGZzU0FIUG9vbC54U3luYyh3cml0ZUZpbGVJZCwgMCk7XG4gICAgICAgICAgICAgICAgb3Bmc1NBSFBvb2wueENsb3NlKHdyaXRlRmlsZUlkKTtcblxuICAgICAgICAgICAgICAgIGlmICh3cml0ZVJlc3VsdCAhPT0gMCkge1xuICAgICAgICAgICAgICAgICAgICB0aHJvdyBuZXcgRXJyb3IoYEZhaWxlZCB0byB3cml0ZSBmaWxlOiAke2FyZ3MuZmlsZW5hbWV9YCk7XG4gICAgICAgICAgICAgICAgfVxuXG4gICAgICAgICAgICAgICAgcmVzdWx0ID0ge1xuICAgICAgICAgICAgICAgICAgICBieXRlc1dyaXR0ZW46IGRhdGEubGVuZ3RoXG4gICAgICAgICAgICAgICAgfTtcbiAgICAgICAgICAgICAgICBicmVhaztcblxuICAgICAgICAgICAgY2FzZSAncGVyc2lzdERpcnR5UGFnZXMnOlxuICAgICAgICAgICAgICAgIC8vIFdyaXRlIG9ubHkgZGlydHkgcGFnZXMgdG8gT1BGUyAoaW5jcmVtZW50YWwgc3luYylcbiAgICAgICAgICAgICAgICBjb25zdCB7IGZpbGVuYW1lLCBwYWdlcyB9ID0gYXJncztcblxuICAgICAgICAgICAgICAgIGlmICghcGFnZXMgfHwgcGFnZXMubGVuZ3RoID09PSAwKSB7XG4gICAgICAgICAgICAgICAgICAgIHJlc3VsdCA9IHsgcGFnZXNXcml0dGVuOiAwIH07XG4gICAgICAgICAgICAgICAgICAgIGJyZWFrO1xuICAgICAgICAgICAgICAgIH1cblxuICAgICAgICAgICAgICAgIGxvZy5pbmZvKGBQZXJzaXN0aW5nICR7cGFnZXMubGVuZ3RofSBkaXJ0eSBwYWdlcyBmb3IgJHtmaWxlbmFtZX1gKTtcblxuICAgICAgICAgICAgICAgIGNvbnN0IFBBR0VfU0laRSA9IDQwOTY7XG4gICAgICAgICAgICAgICAgY29uc3QgU1FMSVRFX09LID0gMDtcbiAgICAgICAgICAgICAgICBjb25zdCBGTEFHU19SRUFEV1JJVEUgPSAweDAyO1xuICAgICAgICAgICAgICAgIGNvbnN0IEZMQUdTX0NSRUFURSA9IDB4MDQ7XG4gICAgICAgICAgICAgICAgY29uc3QgRkxBR1NfTUFJTl9EQiA9IDB4MTAwO1xuXG4gICAgICAgICAgICAgICAgLy8gT3BlbiBmaWxlIGZvciBwYXJ0aWFsIHdyaXRlcyAoY3JlYXRlIGlmIGl0IGRvZXNuJ3QgZXhpc3QgeWV0KVxuICAgICAgICAgICAgICAgIGNvbnN0IHBhcnRpYWxGaWxlSWQgPSBvcGZzU0FIUG9vbC54T3BlbihcbiAgICAgICAgICAgICAgICAgICAgZmlsZW5hbWUsXG4gICAgICAgICAgICAgICAgICAgIEZMQUdTX1JFQURXUklURSB8IEZMQUdTX0NSRUFURSB8IEZMQUdTX01BSU5fREJcbiAgICAgICAgICAgICAgICApO1xuXG4gICAgICAgICAgICAgICAgaWYgKHBhcnRpYWxGaWxlSWQgPCAwKSB7XG4gICAgICAgICAgICAgICAgICAgIHRocm93IG5ldyBFcnJvcihgRmFpbGVkIHRvIG9wZW4gZmlsZSBmb3IgcGFydGlhbCB3cml0ZTogJHtmaWxlbmFtZX1gKTtcbiAgICAgICAgICAgICAgICB9XG5cbiAgICAgICAgICAgICAgICBsZXQgcGFnZXNXcml0dGVuID0gMDtcblxuICAgICAgICAgICAgICAgIHRyeSB7XG4gICAgICAgICAgICAgICAgICAgIC8vIFdyaXRlIGVhY2ggZGlydHkgcGFnZVxuICAgICAgICAgICAgICAgICAgICBmb3IgKGNvbnN0IHBhZ2Ugb2YgcGFnZXMpIHtcbiAgICAgICAgICAgICAgICAgICAgICAgIGNvbnN0IHsgcGFnZU51bWJlciwgZGF0YSB9ID0gcGFnZTtcbiAgICAgICAgICAgICAgICAgICAgICAgIGNvbnN0IG9mZnNldCA9IHBhZ2VOdW1iZXIgKiBQQUdFX1NJWkU7XG4gICAgICAgICAgICAgICAgICAgICAgICBjb25zdCBwYWdlQnVmZmVyID0gbmV3IFVpbnQ4QXJyYXkoZGF0YSk7XG5cbiAgICAgICAgICAgICAgICAgICAgICAgIGNvbnN0IHdyaXRlUmMgPSBvcGZzU0FIUG9vbC54V3JpdGUoXG4gICAgICAgICAgICAgICAgICAgICAgICAgICAgcGFydGlhbEZpbGVJZCxcbiAgICAgICAgICAgICAgICAgICAgICAgICAgICBwYWdlQnVmZmVyLFxuICAgICAgICAgICAgICAgICAgICAgICAgICAgIHBhZ2VCdWZmZXIubGVuZ3RoLFxuICAgICAgICAgICAgICAgICAgICAgICAgICAgIG9mZnNldFxuICAgICAgICAgICAgICAgICAgICAgICAgKTtcblxuICAgICAgICAgICAgICAgICAgICAgICAgaWYgKHdyaXRlUmMgIT09IFNRTElURV9PSykge1xuICAgICAgICAgICAgICAgICAgICAgICAgICAgIHRocm93IG5ldyBFcnJvcihgRmFpbGVkIHRvIHdyaXRlIHBhZ2UgJHtwYWdlTnVtYmVyfSBhdCBvZmZzZXQgJHtvZmZzZXR9YCk7XG4gICAgICAgICAgICAgICAgICAgICAgICB9XG5cbiAgICAgICAgICAgICAgICAgICAgICAgIHBhZ2VzV3JpdHRlbisrO1xuICAgICAgICAgICAgICAgICAgICB9XG5cbiAgICAgICAgICAgICAgICAgICAgLy8gU3luYyB0byBlbnN1cmUgZGF0YSBpcyBwZXJzaXN0ZWRcbiAgICAgICAgICAgICAgICAgICAgb3Bmc1NBSFBvb2wueFN5bmMocGFydGlhbEZpbGVJZCwgMCk7XG5cbiAgICAgICAgICAgICAgICAgICAgbG9nLmluZm8oYFN1Y2Nlc3NmdWxseSB3cm90ZSAke3BhZ2VzV3JpdHRlbn0gcGFnZXNgKTtcblxuICAgICAgICAgICAgICAgIH0gZmluYWxseSB7XG4gICAgICAgICAgICAgICAgICAgIC8vIEFsd2F5cyBjbG9zZSB0aGUgZmlsZVxuICAgICAgICAgICAgICAgICAgICBvcGZzU0FIUG9vbC54Q2xvc2UocGFydGlhbEZpbGVJZCk7XG4gICAgICAgICAgICAgICAgfVxuXG4gICAgICAgICAgICAgICAgcmVzdWx0ID0ge1xuICAgICAgICAgICAgICAgICAgICBwYWdlc1dyaXR0ZW4sXG4gICAgICAgICAgICAgICAgICAgIGJ5dGVzV3JpdHRlbjogcGFnZXNXcml0dGVuICogUEFHRV9TSVpFXG4gICAgICAgICAgICAgICAgfTtcbiAgICAgICAgICAgICAgICBicmVhaztcblxuICAgICAgICAgICAgY2FzZSAnZGVsZXRlRmlsZSc6XG4gICAgICAgICAgICAgICAgY29uc3QgZGVsZXRlUmVzdWx0ID0gb3Bmc1NBSFBvb2wueERlbGV0ZShhcmdzLmZpbGVuYW1lLCAxKTtcbiAgICAgICAgICAgICAgICBpZiAoZGVsZXRlUmVzdWx0ICE9PSAwKSB7XG4gICAgICAgICAgICAgICAgICAgIHRocm93IG5ldyBFcnJvcihgRmFpbGVkIHRvIGRlbGV0ZSBmaWxlOiAke2FyZ3MuZmlsZW5hbWV9YCk7XG4gICAgICAgICAgICAgICAgfVxuICAgICAgICAgICAgICAgIHJlc3VsdCA9IHsgc3VjY2VzczogdHJ1ZSB9O1xuICAgICAgICAgICAgICAgIGJyZWFrO1xuXG4gICAgICAgICAgICBjYXNlICdmaWxlRXhpc3RzJzpcbiAgICAgICAgICAgICAgICBjb25zdCBleGlzdHMgPSBvcGZzU0FIUG9vbC54QWNjZXNzKGFyZ3MuZmlsZW5hbWUsIDApID09PSAwO1xuICAgICAgICAgICAgICAgIHJlc3VsdCA9IHsgZXhpc3RzIH07XG4gICAgICAgICAgICAgICAgYnJlYWs7XG5cbiAgICAgICAgICAgIGRlZmF1bHQ6XG4gICAgICAgICAgICAgICAgdGhyb3cgbmV3IEVycm9yKGBVbmtub3duIG1lc3NhZ2UgdHlwZTogJHt0eXBlfWApO1xuICAgICAgICB9XG5cbiAgICAgICAgY29uc3QgcmVzcG9uc2U6IFdvcmtlclJlc3BvbnNlID0ge1xuICAgICAgICAgICAgaWQsXG4gICAgICAgICAgICBzdWNjZXNzOiB0cnVlLFxuICAgICAgICAgICAgcmVzdWx0XG4gICAgICAgIH07XG4gICAgICAgIHNlbGYucG9zdE1lc3NhZ2UocmVzcG9uc2UpO1xuXG4gICAgfSBjYXRjaCAoZXJyb3IpIHtcbiAgICAgICAgY29uc3QgcmVzcG9uc2U6IFdvcmtlclJlc3BvbnNlID0ge1xuICAgICAgICAgICAgaWQsXG4gICAgICAgICAgICBzdWNjZXNzOiBmYWxzZSxcbiAgICAgICAgICAgIGVycm9yOiBlcnJvciBpbnN0YW5jZW9mIEVycm9yID8gZXJyb3IubWVzc2FnZSA6ICdVbmtub3duIGVycm9yJ1xuICAgICAgICB9O1xuICAgICAgICBzZWxmLnBvc3RNZXNzYWdlKHJlc3BvbnNlKTtcbiAgICB9XG59O1xuXG5sb2cuaW5mbygnV29ya2VyIHNjcmlwdCBsb2FkZWQsIHdhaXRpbmcgZm9yIFNBSFBvb2wgaW5pdGlhbGl6YXRpb24uLi4nKTtcbiJdLAogICJtYXBwaW5ncyI6ICI7QUFXQSxJQUFNLGNBQWM7QUFDcEIsSUFBTSx1QkFBdUI7QUFDN0IsSUFBTSxvQkFBb0I7QUFDMUIsSUFBTSxxQkFBcUI7QUFDM0IsSUFBTSxxQkFBcUIsdUJBQXVCO0FBQ2xELElBQU0sc0JBQXNCO0FBQzVCLElBQU0sdUJBQXVCO0FBQzdCLElBQU0scUJBQXFCO0FBRzNCLElBQU0sc0JBQXNCO0FBQzVCLElBQU0sMkJBQTJCO0FBQ2pDLElBQU0sNEJBQTRCO0FBQ2xDLElBQU0sa0JBQWtCO0FBQ3hCLElBQU0scUJBQXFCO0FBQzNCLElBQU0sNEJBQTRCO0FBQ2xDLElBQU0scUJBQXFCO0FBRTNCLElBQU0sd0JBQ0osc0JBQ0EsMkJBQ0EsNEJBQ0E7QUFF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QkFBb0IsUUFBUSxFQUFFO0FBRWpFLGNBQU0sWUFBWTtBQUNsQixjQUFNQSxhQUFZO0FBQ2xCLGNBQU0sa0JBQWtCO0FBQ3hCLGNBQU0sZUFBZTtBQUNyQixjQUFNLGdCQUFnQjtBQUd0QixjQUFNLGdCQUFnQixZQUFZO0FBQUEsVUFDOUI7QUFBQSxVQUNBLGtCQUFrQixlQUFlO0FBQUEsUUFDckM7QUFFQSxZQUFJLGdCQUFnQixHQUFHO0FBQ25CLGdCQUFNLElBQUksTUFBTSwwQ0FBMEMsUUFBUSxFQUFFO0FBQUEsUUFDeEU7QUFFQSxZQUFJLGVBQWU7QUFFbkIsWUFBSTtBQUVBLHFCQUFXLFFBQVEsT0FBTztBQUN0QixrQkFBTSxFQUFFLFlBQVksTUFBQUMsTUFBSyxJQUFJO0FBQzdCLGtCQUFNLFNBQVMsYUFBYTtBQUM1QixrQkFBTSxhQUFhLElBQUksV0FBV0EsS0FBSTtBQUV0QyxrQkFBTSxVQUFVLFlBQVk7QUFBQSxjQUN4QjtBQUFBLGNBQ0E7QUFBQSxjQUNBLFdBQVc7QUFBQSxjQUNYO0FBQUEsWUFDSjtBQUVBLGdCQUFJLFlBQVlELFlBQVc7QUFDdkIsb0JBQU0sSUFBSSxNQUFNLHdCQUF3QixVQUFVLGNBQWMsTUFBTSxFQUFFO0FBQUEsWUFDNUU7QUFFQTtBQUFBLFVBQ0o7QUFHQSxzQkFBWSxNQUFNLGVBQWUsQ0FBQztBQUVsQyxjQUFJLEtBQUssc0JBQXNCLFlBQVksUUFBUTtBQUFBLFFBRXZELFVBQUU7QUFFRSxzQkFBWSxPQUFPLGFBQWE7QUFBQSxRQUNwQztBQUVBLGlCQUFTO0FBQUEsVUFDTDtBQUFBLFVBQ0EsY0FBYyxlQUFlO0FBQUEsUUFDakM7QUFDQTtBQUFBLE1BRUosS0FBSztBQUNELGNBQU0sZUFBZSxZQUFZLFFBQVEsS0FBSyxVQUFVLENBQUM7QUFDekQsWUFBSSxpQkFBaUIsR0FBRztBQUNwQixnQkFBTSxJQUFJLE1BQU0sMEJBQTBCLEtBQUssUUFBUSxFQUFFO0FBQUEsUUFDN0Q7QUFDQSxpQkFBUyxFQUFFLFNBQVMsS0FBSztBQUN6QjtBQUFBLE1BRUosS0FBSztBQUNELGNBQU0sU0FBUyxZQUFZLFFBQVEsS0FBSyxVQUFVLENBQUMsTUFBTTtBQUN6RCxpQkFBUyxFQUFFLE9BQU87QUFDbEI7QUFBQSxNQUVKO0FBQ0ksY0FBTSxJQUFJLE1BQU0seUJBQXlCLElBQUksRUFBRTtBQUFBLElBQ3ZEO0FBRUEsVUFBTSxXQUEyQjtBQUFBLE1BQzdCO0FBQUEsTUFDQSxTQUFTO0FBQUEsTUFDVDtBQUFBLElBQ0o7QUFDQSxTQUFLLFlBQVksUUFBUTtBQUFBLEVBRTdCLFNBQVMsT0FBTztBQUNaLFVBQU0sV0FBMkI7QUFBQSxNQUM3QjtBQUFBLE1BQ0EsU0FBUztBQUFBLE1BQ1QsT0FBTyxpQkFBaUIsUUFBUSxNQUFNLFVBQVU7QUFBQSxJQUNwRDtBQUNBLFNBQUssWUFBWSxRQUFRO0FBQUEsRUFDN0I7QUFDSjtBQUVBLElBQUksS0FBSyw2REFBNkQ7IiwKICAibmFtZXMiOiBbIlNRTElURV9PSyIsICJkYXRhIl0KfQo=
