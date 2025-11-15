using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace SqliteWasmBlazor.Components.Interop;

/// <summary>
/// JavaScript interop for file operations with MessagePack support
/// Uses JSImport/JSExport for efficient data transfer
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class FileOperationsInterop
{
    private const string ModuleName = "SqliteWasmBlazor.Components.FileOperations";

    /// <summary>
    /// Download a MessagePack file to the browser
    /// Uses ArraySegment with MemoryView to avoid copying data
    /// </summary>
    [JSImport("downloadMessagePackFile", ModuleName)]
    public static partial void DownloadMessagePackFile([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> data, string filename);

    /// <summary>
    /// Initialize the file operations module
    /// Must be called in Program.cs before WebAssemblyHostBuilder.Build()
    /// </summary>
    public static async Task InitializeAsync()
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        try
        {
            await JSHost.ImportAsync(
                ModuleName,
                "../_content/SqliteWasmBlazor.Components/file-operations.js");
            Console.WriteLine("FileOperations module loaded successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading FileOperations module: {ex.Message}");
            throw;
        }
    }
}
