/*
 * vfs_tracking.h
 * SQLite VFS wrapper with dirty page tracking for incremental persistence
 *
 * This VFS sits between SQLite and the underlying VFS (MEMFS) to track
 * which database pages have been modified. This enables incremental sync
 * to OPFS, transferring only changed pages instead of the entire file.
 */

#ifndef VFS_TRACKING_H
#define VFS_TRACKING_H

#include <sqlite3.h>
#include <stdint.h>

#ifdef __EMSCRIPTEN__
#include <emscripten.h>
#define EXPORT_API EMSCRIPTEN_KEEPALIVE
#else
#define EXPORT_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Default SQLite page size (can be overridden) */
#define DEFAULT_PAGE_SIZE 4096

/* File tracking structure */
typedef struct FileTracker {
    char* filename;              /* Database filename */
    uint32_t* dirtyBitmap;       /* Bitmap of dirty pages (1 bit per page) */
    uint32_t bitmapSize;         /* Size of bitmap in uint32_t words */
    uint32_t totalPages;         /* Total number of pages in file */
    uint32_t pageSize;           /* Page size in bytes */
    int isOpen;                  /* Whether file is currently open */
    struct FileTracker* next;    /* Linked list of tracked files */
} FileTracker;

/* Global tracking state */
typedef struct {
    FileTracker* files;          /* Linked list of tracked files */
    sqlite3_vfs* pRealVfs;       /* Underlying VFS (MEMFS) */
    uint32_t defaultPageSize;    /* Default page size */
} VfsTrackingState;

/*
 * Initialize the tracking VFS
 * Must be called before using any tracking functions
 *
 * @param pBaseVfs - The underlying VFS to wrap (e.g., "unix", "memfs")
 * @param pageSize - Default page size (typically 4096)
 * @return SQLITE_OK on success, error code otherwise
 */
EXPORT_API int vfs_tracking_init(const char* baseVfsName, uint32_t pageSize);

/*
 * Shutdown the tracking VFS and free all resources
 */
EXPORT_API void vfs_tracking_shutdown(void);

/*
 * Get dirty pages for a specific file
 *
 * @param filename - Database filename
 * @param pPageCount - Output: number of dirty pages
 * @param ppPages - Output: array of dirty page numbers (caller must free)
 * @return SQLITE_OK on success, error code otherwise
 */
EXPORT_API int vfs_tracking_get_dirty_pages(const char* filename, uint32_t* pPageCount, uint32_t** ppPages);

/*
 * Reset dirty page tracking for a file (mark all clean)
 * Called after successful sync to OPFS
 *
 * @param filename - Database filename
 * @return SQLITE_OK on success, error code otherwise
 */
EXPORT_API int vfs_tracking_reset_dirty(const char* filename);

/*
 * Get file tracker for a specific filename
 * Creates new tracker if doesn't exist
 *
 * @param filename - Database filename
 * @return Pointer to FileTracker or NULL on error
 */
FileTracker* vfs_tracking_get_file(const char* filename);

/*
 * Mark a range of bytes as dirty
 * Internal function called by VFS write operations
 *
 * @param tracker - File tracker
 * @param offset - Byte offset in file
 * @param amount - Number of bytes written
 */
void vfs_tracking_mark_dirty(FileTracker* tracker, int64_t offset, int amount);

#ifdef __cplusplus
}
#endif

#endif /* VFS_TRACKING_H */
