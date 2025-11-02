using Microsoft.JSInterop;
using SQLiteNET.Opfs.Abstractions;
using SQLiteNET.Opfs.Interop;
using System.Runtime.InteropServices;

namespace SQLiteNET.Opfs.Services;

/// <summary>
/// Service for managing SQLite OPFS storage with EF Core integration.
/// Provides automatic persistence of Emscripten MEMFS to OPFS for SQLite databases.
/// </summary>
public class OpfsStorageService : IOpfsStorage, IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private IJSObjectReference? _module;
    private HashSet<string>? _pausedFilesList;
    private bool _vfsTrackingInitialized;

    // Constants for VFS tracking
    private const uint PageSize = 4096;
    private const string BaseVfsName = "unix";  // Default VFS in Emscripten WASM

    public OpfsStorageService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public bool IsReady { get; private set; }
    public bool IsIncrementalSyncEnabled { get; private set; }

    /// <summary>
    /// Set to true to temporarily disable incremental sync and use full sync instead.
    /// Useful for performance testing and debugging.
    /// </summary>
    public bool ForceFullSync { get; set; }

    public async Task<bool> InitializeAsync()
    {
        if (IsReady)
        {
            return true;
        }

        try
        {
            Console.WriteLine("[OpfsStorageService] Starting initialization...");

            // Import the OPFS initializer module
            _module = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/SQLiteNET.Opfs/Components/OpfsInitializer.razor.js");

            // Initialize OPFS Worker
            var result = await _module.InvokeAsync<InitializeResult>("initialize");
            Console.WriteLine($"[OpfsStorageService] Initialize result: Success={result.Success}, Message={result.Message}");

            if (result.Success)
            {
                IsReady = true;
                Console.WriteLine($"[OpfsStorageService] ✓ OPFS initialized: {result.Message}");
                Console.WriteLine($"[OpfsStorageService] ✓ Capacity: {result.Capacity}, Files: {result.FileCount}");

                // Initialize VFS tracking for incremental sync
                try
                {
                    int rc = VfsInterop.Init(BaseVfsName, PageSize);
                    if (rc == 0)  // SQLITE_OK
                    {
                        _vfsTrackingInitialized = true;
                        IsIncrementalSyncEnabled = true;
                        Console.WriteLine($"[OpfsStorageService] ✓ VFS tracking initialized (page size: {PageSize} bytes)");
                    }
                    else
                    {
                        Console.WriteLine($"[OpfsStorageService] ⚠ VFS tracking init failed (rc={rc}), using full sync");
                        IsIncrementalSyncEnabled = false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[OpfsStorageService] ⚠ VFS tracking unavailable: {ex.Message}");
                    Console.WriteLine("[OpfsStorageService] Falling back to full sync mode");
                    IsIncrementalSyncEnabled = false;
                }

                return true;
            }

            await Console.Error.WriteLineAsync($"[OpfsStorageService] Initialization failed: {result.Message}");
            return false;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[OpfsStorageService] Initialization error: {ex.Message}");
            return false;
        }
    }

    public async Task Persist(string fileName)
    {
        if (_pausedFilesList is not null)
        {
            _pausedFilesList.Add(fileName);
            Console.WriteLine($"[OpfsStorageService] Persist paused for: {fileName}");
            return;
        }

        if (_module is null)
        {
            throw new InvalidOperationException("OPFS not initialized");
        }

        // Use incremental sync if available and not force disabled
        if (IsIncrementalSyncEnabled && !ForceFullSync)
        {
            await PersistIncremental(fileName);
        }
        else
        {
            // Fallback to full sync
            string reason = ForceFullSync ? "(forced)" : "(fallback)";
            Console.WriteLine($"[OpfsStorageService] Persisting (full) {reason}: {fileName}");
            await _module.InvokeVoidAsync("persist", fileName);
            Console.WriteLine($"[OpfsStorageService] Persisted: {fileName}");
        }
    }

    /// <summary>
    /// Persist only dirty pages to OPFS using VFS tracking.
    /// This is significantly faster than full sync for large databases with small changes.
    /// </summary>
    private async Task PersistIncremental(string fileName)
    {
        if (!_vfsTrackingInitialized || _module is null)
        {
            throw new InvalidOperationException("VFS tracking not initialized");
        }

        try
        {
            // Get dirty pages from VFS tracking
            int rc = VfsInterop.GetDirtyPages(fileName, out uint pageCount, out IntPtr pagesPtr);

            if (rc != 0)  // Not SQLITE_OK
            {
                Console.WriteLine($"[OpfsStorageService] ⚠ Failed to get dirty pages (rc={rc}), falling back to full sync");
                await _module.InvokeVoidAsync("persist", fileName);
                return;
            }

            if (pageCount == 0)
            {
                Console.WriteLine($"[OpfsStorageService] No dirty pages for {fileName}, skipping persist");
                return;
            }

            // Marshal page numbers to managed array
            uint[] dirtyPages = VfsInterop.MarshalPages(pagesPtr, pageCount);
            VfsInterop.FreePages(pagesPtr);

            Console.WriteLine($"[OpfsStorageService] Persisting (incremental): {fileName} - {pageCount} dirty pages");

            // Read dirty pages from MEMFS
            var pagesToWrite = await ReadDirtyPagesFromMemfs(fileName, dirtyPages);

            if (pagesToWrite.Count == 0)
            {
                Console.WriteLine($"[OpfsStorageService] ⚠ No pages read from MEMFS, skipping");
                return;
            }

            // Send to worker for partial write
            await _module.InvokeVoidAsync("persistDirtyPages", fileName, pagesToWrite);

            // Reset dirty tracking after successful sync
            rc = VfsInterop.ResetDirty(fileName);
            if (rc != 0)
            {
                Console.WriteLine($"[OpfsStorageService] ⚠ Failed to reset dirty pages (rc={rc})");
            }

            // Calculate bandwidth savings
            long dirtyBytes = pageCount * PageSize;
            Console.WriteLine($"[OpfsStorageService] ✓ Persisted {pageCount} pages ({dirtyBytes / 1024} KB)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpfsStorageService] ⚠ Incremental sync failed: {ex.Message}");
            Console.WriteLine("[OpfsStorageService] Falling back to full sync");
            await _module.InvokeVoidAsync("persist", fileName);
        }
    }

    /// <summary>
    /// Read specific pages from Emscripten MEMFS.
    /// </summary>
    private async Task<List<PageData>> ReadDirtyPagesFromMemfs(string fileName, uint[] pageNumbers)
    {
        var pages = new List<PageData>();

        try
        {
            string filePath = $"/{fileName}";

            // Check if file exists and read all pages in one JavaScript call
            var result = await _jsRuntime.InvokeAsync<PageReadResultRaw>("eval",
                $@"(() => {{
                    const fs = window.Blazor?.runtime?.Module?.FS;
                    if (!fs) {{
                        return {{ success: false, error: 'FS not available' }};
                    }}

                    try {{
                        const fileData = fs.readFile('{filePath}');
                        const pageSize = {PageSize};
                        const pageNumbers = {System.Text.Json.JsonSerializer.Serialize(pageNumbers)};
                        const pages = [];

                        for (const pageNum of pageNumbers) {{
                            const offset = pageNum * pageSize;
                            const end = Math.min(offset + pageSize, fileData.length);

                            if (offset < fileData.length) {{
                                pages.push({{
                                    pageNumber: pageNum,
                                    data: Array.from(fileData.subarray(offset, end))
                                }});
                            }}
                        }}

                        return {{ success: true, pages: pages }};
                    }} catch (err) {{
                        return {{ success: false, error: err.message }};
                    }}
                }})()");

            if (!result.Success)
            {
                Console.WriteLine($"[OpfsStorageService] ⚠ Failed to read from MEMFS: {result.Error}");
                return pages;
            }

            if (result.Pages != null)
            {
                foreach (var page in result.Pages)
                {
                    // Convert int array to byte array
                    var data = page.Data?.Select(b => (byte)b).ToArray() ?? Array.Empty<byte>();

                    pages.Add(new PageData
                    {
                        PageNumber = page.PageNumber,
                        Data = data
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpfsStorageService] ⚠ Failed to access MEMFS: {ex.Message}");
        }

        return pages;
    }

    public async Task Load(string fileName)
    {
        if (_module is null)
        {
            throw new InvalidOperationException("OPFS not initialized");
        }

        await _module.InvokeVoidAsync("load", fileName);
    }

    public void PauseAutomaticPersistent()
    {
        _pausedFilesList = [];
    }

    public async Task ResumeAutomaticPersistent()
    {
        if (_pausedFilesList is null)
        {
            throw new InvalidOperationException("Automatic persistence is not paused");
        }

        var files = _pausedFilesList;
        _pausedFilesList = null;

        foreach (var file in files)
        {
            await Persist(file);
        }
    }

    public async Task<string[]> GetFileListAsync()
    {
        if (_module is null)
        {
            throw new InvalidOperationException("OPFS not initialized");
        }

        return await _module.InvokeAsync<string[]>("getFileList");
    }

    public async Task<byte[]> ExportDatabaseAsync(string filename)
    {
        if (_module is null)
        {
            throw new InvalidOperationException("OPFS not initialized");
        }

        var data = await _module.InvokeAsync<int[]>("exportDatabase", filename);
        return data.Select(b => (byte)b).ToArray();
    }

    public async Task<int> ImportDatabaseAsync(string filename, byte[] data)
    {
        if (_module is null)
        {
            throw new InvalidOperationException("OPFS not initialized");
        }

        return await _module.InvokeAsync<int>("importDatabase", filename, data);
    }

    public async Task<int> GetCapacityAsync()
    {
        if (_module is null)
        {
            throw new InvalidOperationException("OPFS not initialized");
        }

        return await _module.InvokeAsync<int>("getCapacity");
    }

    public async Task<int> AddCapacityAsync(int count)
    {
        if (_module is null)
        {
            throw new InvalidOperationException("OPFS not initialized");
        }

        return await _module.InvokeAsync<int>("addCapacity", count);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                // Request worker to release all OPFS handles
                await _module.InvokeVoidAsync("cleanup");
                Console.WriteLine("[OpfsStorageService] Cleanup complete");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OpfsStorageService] Cleanup failed: {ex.Message}");
            }

            await _module.DisposeAsync();
        }

        // Shutdown VFS tracking
        if (_vfsTrackingInitialized)
        {
            try
            {
                VfsInterop.Shutdown();
                Console.WriteLine("[OpfsStorageService] VFS tracking shutdown complete");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OpfsStorageService] VFS tracking shutdown failed: {ex.Message}");
            }
        }
    }

    private class InitializeResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public int Capacity { get; init; }
        public int FileCount { get; init; }
    }

    /// <summary>
    /// Represents a single database page for incremental persistence.
    /// </summary>
    private class PageData
    {
        public uint PageNumber { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Raw page data from JavaScript (uses int array instead of byte array for JSON deserialization).
    /// </summary>
    private class PageDataRaw
    {
        public uint PageNumber { get; init; }
        public int[]? Data { get; init; }
    }

    /// <summary>
    /// Result from reading pages from MEMFS (raw format with int arrays).
    /// </summary>
    private class PageReadResultRaw
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public List<PageDataRaw>? Pages { get; init; }
    }
}