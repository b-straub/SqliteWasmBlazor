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
    private bool _isReady;
    private HashSet<string>? _pausedFilesList;

    public OpfsStorageService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public bool IsReady => _isReady;

    public async Task<bool> InitializeAsync()
    {
        if (_isReady)
        {
            return true;
        }

        try
        {
            // Import the OPFS initializer module (bundled with SQLite WASM)
            _module = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/SQLiteNET.Opfs/Components/OpfsInitializer.razor.js");

            // Initialize OPFS
            var result = await _module.InvokeAsync<InitializeResult>("initialize");

            if (result.Success)
            {
                _isReady = true;
                Console.WriteLine($"OPFS initialized: {result.Message}");
                Console.WriteLine($"Capacity: {result.Capacity}, Files: {result.FileCount}");
                return true;
            }

            await Console.Error.WriteLineAsync($"OPFS initialization failed: {result.Message}");
            return false;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"OPFS initialization error: {ex.Message}");
            return false;
        }
    }

    public async Task Persist(string fileName)
    {
        if (_pausedFilesList is not null)
        {
            _pausedFilesList.Add(fileName);
            return;
        }

        if (_module is null)
        {
            throw new InvalidOperationException("OPFS not initialized");
        }

        await _module.InvokeVoidAsync("persist", fileName);
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
            await _module.DisposeAsync();
        }
    }

    private class InitializeResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int Capacity { get; set; }
        public int FileCount { get; set; }
    }
}
