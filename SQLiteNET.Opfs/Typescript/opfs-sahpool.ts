/**
 * opfs-sahpool.js - Standalone OpfsSAHPool VFS Implementation
 *
 * Based on @sqlite.org/sqlite-wasm's OpfsSAHPool by the SQLite team
 * Which is based on Roy Hashimoto's AccessHandlePoolVFS
 *
 * This is a simplified version for direct integration with our custom e_sqlite3_jsvfs.a
 * No worker messaging - runs directly in the worker context with our WASM module.
 */

// Constants matching SQLite VFS requirements
const SECTOR_SIZE = 4096;
const HEADER_MAX_PATH_SIZE = 512;
const HEADER_FLAGS_SIZE = 4;
const HEADER_DIGEST_SIZE = 8;
const HEADER_CORPUS_SIZE = HEADER_MAX_PATH_SIZE + HEADER_FLAGS_SIZE;
const HEADER_OFFSET_FLAGS = HEADER_MAX_PATH_SIZE;
const HEADER_OFFSET_DIGEST = HEADER_CORPUS_SIZE;
const HEADER_OFFSET_DATA = SECTOR_SIZE;

// SQLite file type flags
const SQLITE_OPEN_MAIN_DB = 0x00000100;
const SQLITE_OPEN_MAIN_JOURNAL = 0x00000800;
const SQLITE_OPEN_SUPER_JOURNAL = 0x00004000;
const SQLITE_OPEN_WAL = 0x00080000;
const SQLITE_OPEN_CREATE = 0x00000004;
const SQLITE_OPEN_DELETEONCLOSE = 0x00000008;
const SQLITE_OPEN_MEMORY = 0x00000080; // Used as FLAG_COMPUTE_DIGEST_V2

const PERSISTENT_FILE_TYPES =
  SQLITE_OPEN_MAIN_DB |
  SQLITE_OPEN_MAIN_JOURNAL |
  SQLITE_OPEN_SUPER_JOURNAL |
  SQLITE_OPEN_WAL;

const FLAG_COMPUTE_DIGEST_V2 = SQLITE_OPEN_MEMORY;
const OPAQUE_DIR_NAME = '.opaque';

// SQLite result codes
const SQLITE_OK = 0;
const SQLITE_ERROR = 1;
const SQLITE_IOERR = 10;
const SQLITE_IOERR_SHORT_READ = 522; // SQLITE_IOERR | (2<<8)
const SQLITE_IOERR_WRITE = 778; // SQLITE_IOERR | (3<<8)
const SQLITE_IOERR_READ = 266; // SQLITE_IOERR | (1<<8)
const SQLITE_CANTOPEN = 14;
const SQLITE_LOCK_NONE = 0;

const getRandomName = () => Math.random().toString(36).slice(2);
const textDecoder = new TextDecoder();
const textEncoder = new TextEncoder();

/**
 * OpfsSAHPool - Pool-based OPFS VFS with Synchronous Access Handles
 *
 * Manages a pool of pre-allocated OPFS files with synchronous access handles.
 * Files are stored with a 4096-byte header containing metadata.
 */
class OpfsSAHPool {
  #dhVfsRoot = null;
  #dhOpaque = null;
  #dhVfsParent = null;

  // Pool management
  #mapSAHToName = new Map();       // SAH -> random OPFS filename
  #mapFilenameToSAH = new Map();   // SQLite path -> SAH
  #availableSAH = new Set();       // Unassociated SAHs ready for use

  // File handle tracking for open files
  #mapFileIdToFile = new Map();    // fileId -> {path, sah, lockType, flags}
  #nextFileId = 1;

  // Header buffer for reading/writing file metadata
  #apBody = new Uint8Array(HEADER_CORPUS_SIZE);
  #dvBody = null;

  vfsDir = null;

  constructor(options = {}) {
    this.vfsDir = options.directory || '.opfs-sahpool';
    this.#dvBody = new DataView(this.#apBody.buffer, this.#apBody.byteOffset);
    this.isReady = this.reset(options.clearOnInit || false)
      .then(() => {
        const capacity = this.getCapacity();
        if (capacity > 0) {
          return Promise.resolve();
        }
        return this.addCapacity(options.initialCapacity || 6);
      });
  }

  log(...args) {
    console.log('[OpfsSAHPool]', ...args);
  }

  warn(...args) {
    console.warn('[OpfsSAHPool]', ...args);
  }

  error(...args) {
    console.error('[OpfsSAHPool]', ...args);
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
      this.setAssociatedPath(ah, '', 0);
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
        this.warn('Error closing handle:', e);
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
      if ('file' === h.kind) {
        files.push([name, h]);
      }
    }

    // Try to acquire handles with retries to allow GC to release old handles
    const maxRetries = 3;
    const retryDelay = 100; // ms

    for (let attempt = 0; attempt < maxRetries; attempt++) {
      if (attempt > 0) {
        this.warn(`Retry ${attempt}/${maxRetries - 1} after ${retryDelay}ms delay...`);
        await new Promise(resolve => setTimeout(resolve, retryDelay * attempt));
      }

      const results = await Promise.allSettled(
        files.map(async ([name, h]) => {
          try {
            const ah = await h.createSyncAccessHandle();
            this.#mapSAHToName.set(ah, name);

            if (clearFiles) {
              ah.truncate(HEADER_OFFSET_DATA);
              this.setAssociatedPath(ah, '', 0);
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
            if (e.name === 'NoModificationAllowedError') {
              // File is locked - will retry or delete on last attempt
              throw e;
            } else {
              this.error('Error acquiring handle:', e);
              this.releaseAccessHandles();
              throw e;
            }
          }
        })
      );

      const locked = results.filter(r =>
        r.status === 'rejected' &&
        r.reason?.name === 'NoModificationAllowedError'
      );

      // If we acquired some handles or this is the last attempt, decide what to do
      if (locked.length === 0 || attempt === maxRetries - 1) {
        if (locked.length > 0) {
          // Last attempt - delete locked files as last resort
          this.warn(`${locked.length} files still locked after ${maxRetries} attempts, deleting...`);
          for (let i = 0; i < files.length; i++) {
            if (results[i].status === 'rejected' && results[i].reason?.name === 'NoModificationAllowedError') {
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

        // Check if we have any capacity after all attempts
        if (this.getCapacity() === 0 && files.length > 0) {
          throw new Error(`Failed to acquire any access handles from ${files.length} files`);
        }

        break; // Exit retry loop
      }

      // Clear maps for next retry
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

    // Check if file should be deleted
    if (
      this.#apBody[0] &&
      (flags & SQLITE_OPEN_DELETEONCLOSE ||
        (flags & PERSISTENT_FILE_TYPES) === 0)
    ) {
      this.warn(`Removing file with unexpected flags ${flags.toString(16)}`);
      this.setAssociatedPath(sah, '', 0);
      return '';
    }

    // Verify digest
    const fileDigest = new Uint32Array(HEADER_DIGEST_SIZE / 4);
    sah.read(fileDigest, { at: HEADER_OFFSET_DIGEST });
    const compDigest = this.computeDigest(this.#apBody, flags);

    if (fileDigest.every((v, i) => v === compDigest[i])) {
      const pathBytes = this.#apBody.findIndex((v) => 0 === v);
      if (0 === pathBytes) {
        sah.truncate(HEADER_OFFSET_DATA);
        return '';
      }
      return textDecoder.decode(this.#apBody.subarray(0, pathBytes));
    } else {
      this.warn('Disassociating file with bad digest');
      this.setAssociatedPath(sah, '', 0);
      return '';
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
      let h1 = 0xdeadbeef;
      let h2 = 0x41c6ce57;
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

    for (const d of this.vfsDir.split('/')) {
      if (d) {
        prev = h;
        h = await h.getDirectoryHandle(d, { create: true });
      }
    }

    this.#dhVfsRoot = h;
    this.#dhVfsParent = prev;
    this.#dhOpaque = await this.#dhVfsRoot.getDirectoryHandle(OPAQUE_DIR_NAME, {
      create: true,
    });

    this.releaseAccessHandles();
    return this.acquireAccessHandles(clearFiles);
  }

  /**
   * Get path (handle both string and pointer)
   */
  getPath(arg) {
    if (typeof arg === 'string') {
      return new URL(arg, 'file://localhost/').pathname;
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
      this.setAssociatedPath(sah, '', 0);
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

      if (!sah && (flags & SQLITE_OPEN_CREATE)) {
        if (this.getFileCount() < this.getCapacity()) {
          sah = this.nextAvailableSAH();
          if (sah) {
            this.setAssociatedPath(sah, path, flags);
          } else {
            this.error('No available SAH in pool');
            return -1;
          }
        } else {
          this.error('SAH pool is full, cannot create file');
          return -1;
        }
      }

      if (!sah) {
        this.error(`File not found: ${path}`);
        return -1;
      }

      // Allocate file ID
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
      this.error('xOpen error:', e);
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
        // Short read - fill rest with zeros
        buffer.fill(0, nRead);
        return SQLITE_IOERR_SHORT_READ;
      }

      return SQLITE_OK;

    } catch (e) {
      this.error('xRead error:', e);
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
      this.error('xWrite error:', e);
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
      this.error('xSync error:', e);
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
      this.error('xTruncate error:', e);
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
      this.error('xFileSize error:', e);
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

      // Don't close the SAH - it's reused in the pool
      // Just remove from open files map
      this.#mapFileIdToFile.delete(fileId);

      this.log(`xClose: fileId ${fileId} (${file.path})`);
      return SQLITE_OK;

    } catch (e) {
      this.error('xClose error:', e);
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
      this.error('xAccess error:', e);
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
      this.error('xDelete error:', e);
      return SQLITE_IOERR;
    }
  }
}

// Export singleton instance
const opfsSAHPool = new OpfsSAHPool({
  directory: '.opfs-sahpool',
  initialCapacity: 6,
  clearOnInit: false
});

// Make available globally for EM_JS hooks
if (typeof globalThis !== 'undefined') {
  globalThis.opfsSAHPool = opfsSAHPool;
}

// ES6 module export
export { OpfsSAHPool, opfsSAHPool };
