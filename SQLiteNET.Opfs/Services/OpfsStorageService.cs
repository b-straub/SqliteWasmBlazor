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

    /// <summary>
    /// Control logging verbosity for OPFS operations.
    /// Default: Warning (only errors and warnings).
    /// </summary>
    public OpfsLogLevel LogLevel { get; set; } = OpfsLogLevel.Warning;

    // Logging helper methods
    private void LogDebug(string message)
    {
        if (LogLevel >= OpfsLogLevel.Debug)
        {
            Console.WriteLine($"[OpfsStorageService] {message}");
        }
    }

    private void LogInfo(string message)
    {
        if (LogLevel >= OpfsLogLevel.Info)
        {
            Console.WriteLine($"[OpfsStorageService] {message}");
        }
    }

    private void LogWarning(string message)
    {
        if (LogLevel >= OpfsLogLevel.Warning)
        {
            Console.WriteLine($"[OpfsStorageService] ⚠ {message}");
        }
    }

    private void LogError(string message)
    {
        if (LogLevel >= OpfsLogLevel.Error)
        {
            Console.Error.WriteLine($"[OpfsStorageService] ❌ {message}");
        }
    }

    public async Task<bool> InitializeAsync()
    {
        if (IsReady)
        {
            return true;
        }

        try
        {
            LogDebug("Starting initialization...");

            // Import the OPFS initializer module
            _module = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/SQLiteNET.Opfs/Components/OpfsInitializer.razor.js");

            // Initialize OPFS Worker
            var result = await _module.InvokeAsync<InitializeResult>("initialize");
            LogDebug($"Initialize result: Success={result.Success}, Message={result.Message}");

            if (result.Success)
            {
                IsReady = true;
                LogInfo($"✓ OPFS initialized: {result.Message}");
                LogInfo($"✓ Capacity: {result.Capacity}, Files: {result.FileCount}");

                // Initialize JSImport for high-performance interop
                try
                {
                    await OpfsJSInterop.InitializeAsync();

                    // Configure JavaScript logging to match C# log level
                    OpfsJSInterop.SetLogLevel(LogLevel);

                    // Configure worker log level
                    try
                    {
                        await _module.InvokeVoidAsync("sendMessageToWorker", "setLogLevel", new { level = (int)LogLevel });
                        LogDebug($"Worker log level set to {LogLevel}");
                    }
                    catch (Exception workerEx)
                    {
                        LogWarning($"Failed to set worker log level: {workerEx.Message}");
                    }

                    LogInfo("✓ JSImport interop initialized");
                }
                catch (Exception ex)
                {
                    LogWarning($"JSImport init failed: {ex.Message}");
                }

                // Initialize VFS tracking for incremental sync
                try
                {
                    int rc = VfsInterop.Init(BaseVfsName, PageSize);
                    if (rc == 0)  // SQLITE_OK
                    {
                        _vfsTrackingInitialized = true;
                        IsIncrementalSyncEnabled = true;
                        LogInfo($"✓ VFS tracking initialized (page size: {PageSize} bytes)");
                    }
                    else
                    {
                        LogWarning($"VFS tracking init failed (rc={rc}), using full sync");
                        IsIncrementalSyncEnabled = false;
                    }
                }
                catch (Exception ex)
                {
                    LogWarning($"VFS tracking unavailable: {ex.Message}");
                    LogInfo("Falling back to full sync mode");
                    IsIncrementalSyncEnabled = false;
                }

                return true;
            }

            LogError($"Initialization failed: {result.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogError($"Initialization error: {ex.Message}");
            return false;
        }
    }

    public async Task Persist(string fileName)
    {
        if (_pausedFilesList is not null)
        {
            _pausedFilesList.Add(fileName);
            LogDebug($"Persist paused for: {fileName}");
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
            LogDebug($"Persisting (full) {reason}: {fileName}");
            await _module.InvokeVoidAsync("persist", fileName);
            LogDebug($"Persisted: {fileName}");
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
                LogWarning($"Failed to get dirty pages (rc={rc}), falling back to full sync");
                await _module.InvokeVoidAsync("persist", fileName);
                return;
            }

            if (pageCount == 0)
            {
                LogDebug($"No dirty pages for {fileName}, skipping persist");
                return;
            }

            // Marshal page numbers to managed array
            uint[] dirtyPages = VfsInterop.MarshalPages(pagesPtr, pageCount);
            VfsInterop.FreePages(pagesPtr);

            LogDebug($"Persisting (incremental): {fileName} - {pageCount} dirty pages");

            // Convert uint[] to int[] for JSImport
            int[] pageNumbersInt = Array.ConvertAll(dirtyPages, p => (int)p);

            // Use JSImport for high-performance zero-copy transfer (synchronous - no await needed)
            using var readResult = OpfsJSInterop.ReadPagesFromMemfs(fileName, pageNumbersInt, (int)PageSize);

            // Check success
            bool success = readResult.GetPropertyAsBoolean("success");
            if (!success)
            {
                string? error = readResult.GetPropertyAsString("error");
                LogWarning($"Failed to read pages from MEMFS: {error}, skipping");
                return;
            }

            // Get pages array
            using var pagesArray = readResult.GetPropertyAsJSObject("pages");
            if (pagesArray is null)
            {
                LogWarning("No pages returned from MEMFS, skipping");
                return;
            }

            // Send to worker for partial write (zero-copy via JSImport)
            using var persistResult = await OpfsJSInterop.PersistDirtyPagesAsync(fileName, pagesArray);

            // Check persist result
            bool persistSuccess = persistResult.GetPropertyAsBoolean("success");
            if (!persistSuccess)
            {
                string? error = persistResult.GetPropertyAsString("error");
                LogWarning($"Failed to persist: {error}");
                return;
            }

            int pagesWritten = persistResult.GetPropertyAsInt32("pagesWritten");
            int bytesWritten = persistResult.GetPropertyAsInt32("bytesWritten");
            LogDebug($"✓ JSImport: Written {pagesWritten} pages ({bytesWritten / 1024} KB)");

            // Reset dirty tracking after successful sync
            rc = VfsInterop.ResetDirty(fileName);
            if (rc != 0)
            {
                LogWarning($"Failed to reset dirty pages (rc={rc})");
            }

            // Calculate bandwidth savings
            long dirtyBytes = pageCount * PageSize;
            LogInfo($"✓ Persisted {pageCount} pages ({dirtyBytes / 1024} KB)");
        }
        catch (Exception ex)
        {
            LogWarning($"Incremental sync failed: {ex.Message}");
            LogInfo("Falling back to full sync");
            await _module.InvokeVoidAsync("persist", fileName);
        }
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
                LogInfo("Cleanup complete");
            }
            catch (Exception ex)
            {
                LogWarning($"Cleanup failed: {ex.Message}");
            }

            await _module.DisposeAsync();
        }

        // Shutdown VFS tracking
        if (_vfsTrackingInitialized)
        {
            try
            {
                VfsInterop.Shutdown();
                LogInfo("VFS tracking shutdown complete");
            }
            catch (Exception ex)
            {
                LogWarning($"VFS tracking shutdown failed: {ex.Message}");
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
}