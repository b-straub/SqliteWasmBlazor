using System.Runtime.InteropServices;

namespace SQLiteNET.Opfs.Interop;

/// <summary>
/// P/Invoke wrappers for SQLite VFS tracking native library.
/// Provides access to dirty page tracking functionality for incremental OPFS persistence.
/// </summary>
public static class VfsInterop
{
    private const string LibraryName = "e_sqlite3";

    /// <summary>
    /// Initialize the VFS tracking system.
    /// Must be called once at startup before opening any databases.
    /// </summary>
    /// <param name="baseVfsName">Name of underlying VFS (typically "memfs" for WASM)</param>
    /// <param name="pageSize">Database page size in bytes (typically 4096)</param>
    /// <returns>SQLite result code (0 = SQLITE_OK)</returns>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vfs_tracking_init")]
    public static extern int Init(
        [MarshalAs(UnmanagedType.LPStr)] string baseVfsName,
        uint pageSize
    );

    /// <summary>
    /// Shutdown the VFS tracking system and free all resources.
    /// Should be called on application shutdown.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vfs_tracking_shutdown")]
    public static extern void Shutdown();

    /// <summary>
    /// Get list of dirty pages for a specific database file.
    /// The returned array must be freed using FreePages() after use.
    /// </summary>
    /// <param name="filename">Database filename (e.g., "TodoDb.db")</param>
    /// <param name="pageCount">Output: number of dirty pages</param>
    /// <param name="pages">Output: pointer to array of dirty page numbers</param>
    /// <returns>SQLite result code (0 = SQLITE_OK)</returns>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vfs_tracking_get_dirty_pages")]
    public static extern int GetDirtyPages(
        [MarshalAs(UnmanagedType.LPStr)] string filename,
        out uint pageCount,
        out IntPtr pages
    );

    /// <summary>
    /// Reset dirty page tracking for a database file.
    /// Call this after successfully syncing dirty pages to OPFS.
    /// </summary>
    /// <param name="filename">Database filename (e.g., "TodoDb.db")</param>
    /// <returns>SQLite result code (0 = SQLITE_OK)</returns>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vfs_tracking_reset_dirty")]
    public static extern int ResetDirty(
        [MarshalAs(UnmanagedType.LPStr)] string filename
    );

    /// <summary>
    /// Helper method to marshal dirty page array from native memory.
    /// </summary>
    /// <param name="pagesPtr">Pointer to native array</param>
    /// <param name="count">Number of pages</param>
    /// <returns>Managed array of page numbers</returns>
    public static uint[] MarshalPages(IntPtr pagesPtr, uint count)
    {
        if (pagesPtr == IntPtr.Zero || count == 0)
        {
            return Array.Empty<uint>();
        }

        var pages = new uint[count];
        Marshal.Copy(pagesPtr, (int[])(object)pages, 0, (int)count);
        return pages;
    }

    /// <summary>
    /// Free native memory allocated for dirty pages array.
    /// Must be called after GetDirtyPages() to avoid memory leaks.
    /// </summary>
    /// <param name="pagesPtr">Pointer to free</param>
    public static void FreePages(IntPtr pagesPtr)
    {
        if (pagesPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(pagesPtr);
        }
    }
}
