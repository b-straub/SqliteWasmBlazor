using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using SQLiteNET.Opfs.Abstractions;

namespace SQLiteNET.Opfs.Interop;

/// <summary>
/// High-performance JSImport interop for OPFS operations.
/// Uses source-generated marshalling for zero-copy data transfers.
/// </summary>
[SupportedOSPlatform("browser")]
public partial class OpfsJSInterop
{
    private const string ModuleName = "opfsInterop";

    private static bool _isInitialized;

    /// <summary>
    /// Initialize and register the OPFS JavaScript module.
    /// Must be called once before using any JSImport methods.
    /// </summary>
    public static async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        await JSHost.ImportAsync(
            ModuleName,
            "/_content/SQLiteNET.Opfs/opfs-interop.js");

        _isInitialized = true;
    }

    /// <summary>
    /// Set the JavaScript log level to match C# logging configuration.
    /// </summary>
    public static void SetLogLevel(OpfsLogLevel logLevel)
    {
        try
        {
            // Access the global __opfsLogger object exposed by opfs-logger.ts
            var logger = System.Runtime.InteropServices.JavaScript.JSHost.GlobalThis.GetPropertyAsJSObject("__opfsLogger");
            if (logger != null)
            {
                using (logger)
                {
                    // Call setLogLevel method
                    logger.SetProperty("logLevel", (int)logLevel);
                }
            }
        }
        catch
        {
            // Logger not available yet - will be set later
        }
    }

    /// <summary>
    /// Read specific pages from Emscripten MEMFS (synchronous).
    /// Returns a JSObject that can be queried for result data.
    /// </summary>
    [JSImport("readPagesFromMemfs", ModuleName)]
    public static partial JSObject ReadPagesFromMemfs(
        string filename,
        int[] pageNumbers,
        int pageSize);

    /// <summary>
    /// Persist dirty pages to OPFS (incremental sync).
    /// Accepts a JSObject containing page data.
    /// </summary>
    [JSImport("persistDirtyPages", ModuleName)]
    public static partial Task<JSObject> PersistDirtyPagesAsync(
        string filename,
        JSObject pages);
}

/// <summary>
/// Represents a single database page for transfer.
/// </summary>
public struct PageData
{
    public uint PageNumber { get; set; }
    public byte[] Data { get; set; }
}
