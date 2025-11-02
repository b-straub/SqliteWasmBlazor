/*
 * vfs_tracking.c
 * Implementation of SQLite VFS wrapper with dirty page tracking
 */

#include "vfs_tracking.h"
#include <string.h>
#include <stdlib.h>
#include <stdio.h>

/* Global tracking state */
static VfsTrackingState g_state = {0};

/* Forward declarations for VFS methods */
static int trackingOpen(sqlite3_vfs*, const char *zName, sqlite3_file*, int flags, int *pOutFlags);
static int trackingDelete(sqlite3_vfs*, const char *zName, int syncDir);
static int trackingAccess(sqlite3_vfs*, const char *zName, int flags, int *pResOut);
static int trackingFullPathname(sqlite3_vfs*, const char *zName, int nOut, char *zOut);

/* Forward declarations for file methods */
static int fileClose(sqlite3_file*);
static int fileRead(sqlite3_file*, void*, int iAmt, sqlite3_int64 iOfst);
static int fileWrite(sqlite3_file*, const void*, int iAmt, sqlite3_int64 iOfst);
static int fileTruncate(sqlite3_file*, sqlite3_int64 size);
static int fileSync(sqlite3_file*, int flags);
static int fileFileSize(sqlite3_file*, sqlite3_int64 *pSize);
static int fileLock(sqlite3_file*, int);
static int fileUnlock(sqlite3_file*, int);
static int fileCheckReservedLock(sqlite3_file*, int *pResOut);
static int fileFileControl(sqlite3_file*, int op, void *pArg);
static int fileSectorSize(sqlite3_file*);
static int fileDeviceCharacteristics(sqlite3_file*);

/* VFS method table */
static sqlite3_vfs trackingVfs = {
    3,                            /* iVersion */
    0,                            /* szOsFile (filled in init) */
    0,                            /* mxPathname (filled in init) */
    0,                            /* pNext */
    "tracking",                   /* zName */
    0,                            /* pAppData (points to real VFS) */
    trackingOpen,                 /* xOpen */
    trackingDelete,               /* xDelete */
    trackingAccess,               /* xAccess */
    trackingFullPathname,         /* xFullPathname */
    0,                            /* xDlOpen */
    0,                            /* xDlError */
    0,                            /* xDlSym */
    0,                            /* xDlClose */
    0,                            /* xRandomness */
    0,                            /* xSleep */
    0,                            /* xCurrentTime */
    0,                            /* xGetLastError */
    0,                            /* xCurrentTimeInt64 */
    0,                            /* xSetSystemCall */
    0,                            /* xGetSystemCall */
    0,                            /* xNextSystemCall */
};

/* File method table */
static const sqlite3_io_methods trackingIoMethods = {
    3,                              /* iVersion */
    fileClose,                      /* xClose */
    fileRead,                       /* xRead */
    fileWrite,                      /* xWrite */
    fileTruncate,                   /* xTruncate */
    fileSync,                       /* xSync */
    fileFileSize,                   /* xFileSize */
    fileLock,                       /* xLock */
    fileUnlock,                     /* xUnlock */
    fileCheckReservedLock,          /* xCheckReservedLock */
    fileFileControl,                /* xFileControl */
    fileSectorSize,                 /* xSectorSize */
    fileDeviceCharacteristics,      /* xDeviceCharacteristics */
    0,                              /* xShmMap */
    0,                              /* xShmLock */
    0,                              /* xShmBarrier */
    0,                              /* xShmUnmap */
    0,                              /* xFetch */
    0,                              /* xUnfetch */
};

/* Tracking file structure */
typedef struct TrackingFile {
    sqlite3_file base;              /* Base class - must be first */
    sqlite3_file* pReal;            /* Real file from underlying VFS */
    FileTracker* pTracker;          /* Associated file tracker */
} TrackingFile;

/*
 * Initialize the tracking VFS
 */
int vfs_tracking_init(const char* baseVfsName, uint32_t pageSize)
{
    sqlite3_vfs* pRealVfs;

    if (g_state.pRealVfs != NULL)
    {
        /* Already initialized */
        return SQLITE_OK;
    }

    /* Find the underlying VFS */
    pRealVfs = sqlite3_vfs_find(baseVfsName);
    if (pRealVfs == NULL)
    {
        fprintf(stderr, "[VFS Tracking] Base VFS '%s' not found\n", baseVfsName);
        return SQLITE_ERROR;
    }

    /* Initialize state */
    g_state.pRealVfs = pRealVfs;
    g_state.files = NULL;
    g_state.defaultPageSize = pageSize > 0 ? pageSize : DEFAULT_PAGE_SIZE;

    /* Configure tracking VFS - copy all methods from real VFS first */
    trackingVfs.szOsFile = sizeof(TrackingFile) + pRealVfs->szOsFile;
    trackingVfs.mxPathname = pRealVfs->mxPathname;
    trackingVfs.pAppData = pRealVfs;

    /* Delegate all other VFS methods to the real VFS */
    trackingVfs.xDlOpen = pRealVfs->xDlOpen;
    trackingVfs.xDlError = pRealVfs->xDlError;
    trackingVfs.xDlSym = pRealVfs->xDlSym;
    trackingVfs.xDlClose = pRealVfs->xDlClose;
    trackingVfs.xRandomness = pRealVfs->xRandomness;
    trackingVfs.xSleep = pRealVfs->xSleep;
    trackingVfs.xCurrentTime = pRealVfs->xCurrentTime;
    trackingVfs.xGetLastError = pRealVfs->xGetLastError;
    trackingVfs.xCurrentTimeInt64 = pRealVfs->xCurrentTimeInt64;
    trackingVfs.xSetSystemCall = pRealVfs->xSetSystemCall;
    trackingVfs.xGetSystemCall = pRealVfs->xGetSystemCall;
    trackingVfs.xNextSystemCall = pRealVfs->xNextSystemCall;

    /* Register the tracking VFS as default (makeDflt=1) */
    /* This ensures all new SQLite connections use our tracking wrapper */
    int rc = sqlite3_vfs_register(&trackingVfs, 1);
    if (rc != SQLITE_OK)
    {
        fprintf(stderr, "[VFS Tracking] Failed to register VFS: %d\n", rc);
        return rc;
    }

    printf("[VFS Tracking] Initialized with page size %u (default VFS)\n", g_state.defaultPageSize);
    return SQLITE_OK;
}

/*
 * Shutdown the tracking VFS
 */
void vfs_tracking_shutdown(void)
{
    FileTracker* pFile = g_state.files;

    while (pFile != NULL)
    {
        FileTracker* pNext = pFile->next;

        if (pFile->filename)
        {
            free(pFile->filename);
        }

        if (pFile->dirtyBitmap)
        {
            free(pFile->dirtyBitmap);
        }

        free(pFile);
        pFile = pNext;
    }

    sqlite3_vfs_unregister(&trackingVfs);

    g_state.files = NULL;
    g_state.pRealVfs = NULL;

    printf("[VFS Tracking] Shutdown complete\n");
}

/*
 * Get or create file tracker
 */
FileTracker* vfs_tracking_get_file(const char* filename)
{
    FileTracker* pFile;
    const char* normalizedName;

    if (filename == NULL)
    {
        return NULL;
    }

    /* Normalize path - strip leading slash for consistency */
    normalizedName = (filename[0] == '/') ? filename + 1 : filename;

    /* Search for existing tracker */
    pFile = g_state.files;
    while (pFile != NULL)
    {
        if (strcmp(pFile->filename, normalizedName) == 0)
        {
            return pFile;
        }
        pFile = pFile->next;
    }

    /* Create new tracker */
    pFile = (FileTracker*)calloc(1, sizeof(FileTracker));
    if (pFile == NULL)
    {
        return NULL;
    }

    pFile->filename = strdup(normalizedName);
    pFile->pageSize = g_state.defaultPageSize;
    pFile->totalPages = 0;
    pFile->bitmapSize = 0;
    pFile->dirtyBitmap = NULL;
    pFile->isOpen = 0;

    /* Add to linked list */
    pFile->next = g_state.files;
    g_state.files = pFile;

    return pFile;
}

/*
 * Mark a range of bytes as dirty
 */
void vfs_tracking_mark_dirty(FileTracker* tracker, int64_t offset, int amount)
{
    uint32_t startPage, endPage, i;

    if (tracker == NULL || amount <= 0)
    {
        return;
    }

    /* Calculate affected pages */
    startPage = (uint32_t)(offset / tracker->pageSize);
    endPage = (uint32_t)((offset + amount - 1) / tracker->pageSize);

    /* Ensure bitmap is large enough */
    uint32_t requiredPages = endPage + 1;
    if (requiredPages > tracker->totalPages)
    {
        uint32_t newBitmapSize = (requiredPages + 31) / 32;  /* Round up to uint32_t */

        if (newBitmapSize > tracker->bitmapSize)
        {
            uint32_t* newBitmap = (uint32_t*)realloc(
                tracker->dirtyBitmap,
                newBitmapSize * sizeof(uint32_t)
            );

            if (newBitmap == NULL)
            {
                fprintf(stderr, "[VFS Tracking] Failed to allocate bitmap\n");
                return;
            }

            /* Zero out new portion */
            memset(newBitmap + tracker->bitmapSize, 0,
                   (newBitmapSize - tracker->bitmapSize) * sizeof(uint32_t));

            tracker->dirtyBitmap = newBitmap;
            tracker->bitmapSize = newBitmapSize;
        }

        tracker->totalPages = requiredPages;
    }

    /* Mark pages as dirty */
    for (i = startPage; i <= endPage; i++)
    {
        uint32_t wordIndex = i / 32;
        uint32_t bitIndex = i % 32;
        tracker->dirtyBitmap[wordIndex] |= (1U << bitIndex);
    }
}

/*
 * Get dirty pages
 */
int vfs_tracking_get_dirty_pages(const char* filename, uint32_t* pPageCount, uint32_t** ppPages)
{
    FileTracker* tracker;
    uint32_t i, dirtyCount = 0;
    uint32_t* pages;

    if (filename == NULL || pPageCount == NULL || ppPages == NULL)
    {
        return SQLITE_ERROR;
    }

    tracker = vfs_tracking_get_file(filename);
    if (tracker == NULL || tracker->dirtyBitmap == NULL)
    {
        *pPageCount = 0;
        *ppPages = NULL;
        return SQLITE_OK;
    }

    /* Count dirty pages */
    for (i = 0; i < tracker->totalPages; i++)
    {
        uint32_t wordIndex = i / 32;
        uint32_t bitIndex = i % 32;

        if (tracker->dirtyBitmap[wordIndex] & (1U << bitIndex))
        {
            dirtyCount++;
        }
    }

    if (dirtyCount == 0)
    {
        *pPageCount = 0;
        *ppPages = NULL;
        return SQLITE_OK;
    }

    /* Allocate array for page numbers */
    pages = (uint32_t*)malloc(dirtyCount * sizeof(uint32_t));
    if (pages == NULL)
    {
        return SQLITE_NOMEM;
    }

    /* Fill array with dirty page numbers */
    uint32_t pageIndex = 0;
    for (i = 0; i < tracker->totalPages; i++)
    {
        uint32_t wordIndex = i / 32;
        uint32_t bitIndex = i % 32;

        if (tracker->dirtyBitmap[wordIndex] & (1U << bitIndex))
        {
            pages[pageIndex++] = i;
        }
    }

    *pPageCount = dirtyCount;
    *ppPages = pages;

    printf("[VFS Tracking] Found %u dirty pages for '%s'\n", dirtyCount, filename);

    return SQLITE_OK;
}

/*
 * Reset dirty page tracking
 */
int vfs_tracking_reset_dirty(const char* filename)
{
    FileTracker* tracker;

    if (filename == NULL)
    {
        return SQLITE_ERROR;
    }

    tracker = vfs_tracking_get_file(filename);
    if (tracker == NULL || tracker->dirtyBitmap == NULL)
    {
        return SQLITE_OK;
    }

    /* Zero out bitmap */
    memset(tracker->dirtyBitmap, 0, tracker->bitmapSize * sizeof(uint32_t));

    printf("[VFS Tracking] Reset dirty pages for '%s'\n", filename);

    return SQLITE_OK;
}

/*
 * VFS xOpen method
 */
static int trackingOpen(
    sqlite3_vfs* pVfs,
    const char* zName,
    sqlite3_file* pFile,
    int flags,
    int* pOutFlags
)
{
    TrackingFile* p = (TrackingFile*)pFile;
    sqlite3_vfs* pRealVfs = (sqlite3_vfs*)pVfs->pAppData;
    int rc;

    /* Allocate space for real file after our structure */
    p->pReal = (sqlite3_file*)&p[1];

    /* Open the real file */
    rc = pRealVfs->xOpen(pRealVfs, zName, p->pReal, flags, pOutFlags);

    if (rc == SQLITE_OK)
    {
        /* Set up tracking */
        p->base.pMethods = &trackingIoMethods;

        if (zName != NULL)
        {
            p->pTracker = vfs_tracking_get_file(zName);
            if (p->pTracker != NULL)
            {
                p->pTracker->isOpen = 1;
            }
        }
        else
        {
            p->pTracker = NULL;
        }
    }

    return rc;
}

/* VFS pass-through methods */

static int trackingDelete(sqlite3_vfs* pVfs, const char* zName, int syncDir)
{
    sqlite3_vfs* pRealVfs = (sqlite3_vfs*)pVfs->pAppData;
    return pRealVfs->xDelete(pRealVfs, zName, syncDir);
}

static int trackingAccess(sqlite3_vfs* pVfs, const char* zName, int flags, int* pResOut)
{
    sqlite3_vfs* pRealVfs = (sqlite3_vfs*)pVfs->pAppData;
    return pRealVfs->xAccess(pRealVfs, zName, flags, pResOut);
}

static int trackingFullPathname(sqlite3_vfs* pVfs, const char* zName, int nOut, char* zOut)
{
    sqlite3_vfs* pRealVfs = (sqlite3_vfs*)pVfs->pAppData;
    return pRealVfs->xFullPathname(pRealVfs, zName, nOut, zOut);
}

/* File method implementations */

static int fileClose(sqlite3_file* pFile)
{
    TrackingFile* p = (TrackingFile*)pFile;
    int rc = p->pReal->pMethods->xClose(p->pReal);

    if (p->pTracker != NULL)
    {
        p->pTracker->isOpen = 0;
    }

    return rc;
}

static int fileRead(sqlite3_file* pFile, void* zBuf, int iAmt, sqlite3_int64 iOfst)
{
    TrackingFile* p = (TrackingFile*)pFile;
    return p->pReal->pMethods->xRead(p->pReal, zBuf, iAmt, iOfst);
}

static int fileWrite(sqlite3_file* pFile, const void* zBuf, int iAmt, sqlite3_int64 iOfst)
{
    TrackingFile* p = (TrackingFile*)pFile;
    int rc;

    /* Perform the write */
    rc = p->pReal->pMethods->xWrite(p->pReal, zBuf, iAmt, iOfst);

    /* Track dirty pages on successful write */
    if (rc == SQLITE_OK && p->pTracker != NULL)
    {
        vfs_tracking_mark_dirty(p->pTracker, iOfst, iAmt);
    }

    return rc;
}

static int fileTruncate(sqlite3_file* pFile, sqlite3_int64 size)
{
    TrackingFile* p = (TrackingFile*)pFile;
    int rc = p->pReal->pMethods->xTruncate(p->pReal, size);

    /* Truncate could affect pages - mark as modified */
    if (rc == SQLITE_OK && p->pTracker != NULL)
    {
        /* Mark the truncation point onwards as "dirty" in case file shrinks */
        vfs_tracking_mark_dirty(p->pTracker, size, 1);
    }

    return rc;
}

static int fileSync(sqlite3_file* pFile, int flags)
{
    TrackingFile* p = (TrackingFile*)pFile;
    return p->pReal->pMethods->xSync(p->pReal, flags);
}

static int fileFileSize(sqlite3_file* pFile, sqlite3_int64* pSize)
{
    TrackingFile* p = (TrackingFile*)pFile;
    return p->pReal->pMethods->xFileSize(p->pReal, pSize);
}

static int fileLock(sqlite3_file* pFile, int eLock)
{
    TrackingFile* p = (TrackingFile*)pFile;
    return p->pReal->pMethods->xLock(p->pReal, eLock);
}

static int fileUnlock(sqlite3_file* pFile, int eLock)
{
    TrackingFile* p = (TrackingFile*)pFile;
    return p->pReal->pMethods->xUnlock(p->pReal, eLock);
}

static int fileCheckReservedLock(sqlite3_file* pFile, int* pResOut)
{
    TrackingFile* p = (TrackingFile*)pFile;
    return p->pReal->pMethods->xCheckReservedLock(p->pReal, pResOut);
}

static int fileFileControl(sqlite3_file* pFile, int op, void* pArg)
{
    TrackingFile* p = (TrackingFile*)pFile;
    return p->pReal->pMethods->xFileControl(p->pReal, op, pArg);
}

static int fileSectorSize(sqlite3_file* pFile)
{
    TrackingFile* p = (TrackingFile*)pFile;
    return p->pReal->pMethods->xSectorSize(p->pReal);
}

static int fileDeviceCharacteristics(sqlite3_file* pFile)
{
    TrackingFile* p = (TrackingFile*)pFile;
    return p->pReal->pMethods->xDeviceCharacteristics(p->pReal);
}
