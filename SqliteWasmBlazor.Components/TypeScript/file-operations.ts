// file-operations.ts
// File download operation for Blazor WebAssembly
// Upload is handled by Blazor's InputFile component
// Serialization/deserialization handled by MessagePack-CSharp with streaming

/**
 * IMemoryView interface from dotnet runtime
 * Represents a view over managed memory (Span/ArraySegment)
 */
interface IMemoryView {
    slice(): Uint8Array;
    slice(start: number): Uint8Array;
    slice(start: number, end: number): Uint8Array;
}

/**
 * Download a MessagePack file
 * @param memoryView - MemoryView from C# ArraySegment<byte> (no copy marshaling)
 * @param filename - Name of the file to download
 */
function downloadMessagePackFile(memoryView: IMemoryView, filename: string): void {
    const startTime = performance.now();
    console.log(`[${new Date().toISOString().substring(11, 23)}] downloadMessagePackFile: START - filename=${filename}`);

    try {
        // Use .slice() to get actual Uint8Array from MemoryView
        // This creates a copy, but avoids double-copy during marshaling
        const sliceStart = performance.now();
        const data = memoryView.slice();
        console.log(`[${new Date().toISOString().substring(11, 23)}] downloadMessagePackFile: memoryView.slice() took ${(performance.now() - sliceStart).toFixed(2)}ms, size=${(data.length / 1024 / 1024).toFixed(2)}MB`);

        const blobStart = performance.now();
        const blob = new Blob([data as BlobPart], { type: 'application/x-msgpack' });
        console.log(`[${new Date().toISOString().substring(11, 23)}] downloadMessagePackFile: Blob creation took ${(performance.now() - blobStart).toFixed(2)}ms`);

        const urlStart = performance.now();
        const url = URL.createObjectURL(blob);
        console.log(`[${new Date().toISOString().substring(11, 23)}] downloadMessagePackFile: URL.createObjectURL took ${(performance.now() - urlStart).toFixed(2)}ms`);

        const linkStart = performance.now();
        const link = document.createElement('a');
        link.href = url;
        link.download = filename;
        link.style.display = 'none';

        document.body.appendChild(link);
        link.click();

        document.body.removeChild(link);
        console.log(`[${new Date().toISOString().substring(11, 23)}] downloadMessagePackFile: DOM manipulation + click took ${(performance.now() - linkStart).toFixed(2)}ms`);

        URL.revokeObjectURL(url);

        console.log(`[${new Date().toISOString().substring(11, 23)}] downloadMessagePackFile: TOTAL TIME ${(performance.now() - startTime).toFixed(2)}ms for ${(data.length / 1024 / 1024).toFixed(2)}MB`);
    } catch (error) {
        console.error('Error downloading file:', error);
        throw error;
    }
}

export { downloadMessagePackFile };

console.log('File operations module loaded');
