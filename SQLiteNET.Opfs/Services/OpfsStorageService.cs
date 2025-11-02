using Microsoft.JSInterop;
using SQLiteNET.Opfs.Abstractions;

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

    public OpfsStorageService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public bool IsReady { get; private set; }

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

        Console.WriteLine($"[OpfsStorageService] Persisting: {fileName}");
        await _module.InvokeVoidAsync("persist", fileName);
        Console.WriteLine($"[OpfsStorageService] Persisted: {fileName}");
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
    }

    private class InitializeResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public int Capacity { get; init; }
        public int FileCount { get; init; }
    }
}