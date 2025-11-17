using MessagePack;
using Microsoft.Extensions.Logging;

namespace SqliteWasmBlazor.Components.Interop;

/// <summary>
/// Generic streaming serialization helper for entity collections using MessagePack with optional LZ4 compression
/// All methods are truly async without Task.Run wrappers
/// </summary>
public static class MessagePackSerializer<T>
{
    /// <summary>
    /// MessagePack options WITHOUT compression (for performance testing)
    /// LZ4BlockArray compression is DISABLED to measure raw serialization performance
    /// To enable compression, uncomment the .WithCompression line below
    /// </summary>
    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard;
        //.WithCompression(MessagePackCompression.Lz4BlockArray); // DISABLED for testing

    /// <summary>
    /// Serialize entities to a stream one by one (chunked format)
    /// First writes a header with schema metadata (hash computed automatically), then each entity as a separate MessagePack object
    /// This allows streaming without loading entire dataset into memory
    /// </summary>
    /// <param name="items">Items to serialize</param>
    /// <param name="stream">Target stream</param>
    /// <param name="appIdentifier">Optional application identifier</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <param name="progress">Optional progress callback (current, total)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public static async Task SerializeStreamAsync(
        IEnumerable<T> items,
        Stream stream,
        string? appIdentifier = null,
        ILogger? logger = null,
        IProgress<(int current, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var itemList = items.ToList();
        var total = itemList.Count;
        var current = 0;

        logger?.LogDebug("Starting serialization of {Count} {Type} items", total, typeof(T).Name);

        // Write header first for schema validation (hash computed automatically)
        var header = MessagePackFileHeader.Create<T>(total, appIdentifier);
        logger?.LogDebug("Writing header: Type={Type}, SchemaHash={Hash}, Records={Count}",
            header.DataType, header.SchemaHash, header.RecordCount);

        await MessagePackSerializer.SerializeAsync(stream, header, Options, cancellationToken);

        foreach (var item in itemList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await MessagePackSerializer.SerializeAsync(stream, item, Options, cancellationToken);

            current++;
            progress?.Report((current, total));
        }

        logger?.LogInformation("Serialized {Count} {Type} items to {Bytes} bytes",
            total, typeof(T).Name, stream.Length);
    }

    /// <summary>
    /// Deserialize entities from a stream one by one (chunked format)
    /// First validates the header for schema compatibility, then reads each entity as a separate MessagePack object
    /// Processes items in batches without loading entire dataset into memory
    /// </summary>
    /// <param name="stream">Source stream</param>
    /// <param name="onBatch">Callback invoked for each batch of items</param>
    /// <param name="expectedSchemaHash">Expected schema hash (or null to skip schema check)</param>
    /// <param name="expectedAppIdentifier">Expected application identifier (or null to skip app check)</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <param name="batchSize">Number of items per batch</param>
    /// <param name="progress">Optional progress callback (current, total)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Total number of items deserialized</returns>
    public static async Task<int> DeserializeStreamAsync(
        Stream stream,
        Func<List<T>, Task> onBatch,
        string? expectedSchemaHash = null,
        string? expectedAppIdentifier = null,
        ILogger? logger = null,
        int batchSize = 1000,
        IProgress<(int current, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (onBatch is null)
        {
            throw new ArgumentNullException(nameof(onBatch));
        }

        logger?.LogDebug("Starting deserialization with batch size {BatchSize}", batchSize);
        var streamReader = new MessagePackStreamReader(stream);

        // Read and validate header first
        var headerData = await streamReader.ReadAsync(cancellationToken);
        if (headerData is null)
        {
            throw new InvalidOperationException("File is empty or missing header");
        }

        MessagePackFileHeader header;
        try
        {
            var headerSequence = headerData.Value;
            header = MessagePackSerializer.Deserialize<MessagePackFileHeader>(in headerSequence, Options, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Invalid or missing file header. This file may not be a valid export from this application.", ex);
        }

        logger?.LogDebug("Read header: Type={Type}, SchemaHash={Hash}, Records={Count}, Exported={ExportDate}",
            header.DataType, header.SchemaHash, header.RecordCount, header.ExportedAt);

        // Validate header compatibility
        var expectedType = typeof(T).FullName ?? typeof(T).Name;
        header.Validate(expectedType, expectedSchemaHash, expectedAppIdentifier);

        logger?.LogInformation("Importing {Count} {Type} records (schema hash {Hash})",
            header.RecordCount, typeof(T).Name, header.SchemaHash);

        var batch = new List<T>(batchSize);
        var totalCount = 0;

        while (await streamReader.ReadAsync(cancellationToken) is { } msgpack)
        {
            var item = MessagePackSerializer.Deserialize<T>(msgpack, Options, cancellationToken);

            if (item is null)
            {
                throw new InvalidOperationException($"Deserialized {typeof(T).Name} is null");
            }

            batch.Add(item);
            totalCount++;

            if (batch.Count >= batchSize)
            {
                await onBatch(batch);
                progress?.Report((totalCount, -1));
                batch = new List<T>(batchSize);
            }
        }

        if (batch.Count > 0)
        {
            await onBatch(batch);
            progress?.Report((totalCount, -1));
        }

        logger?.LogInformation("Deserialized {Count} {Type} items", totalCount, typeof(T).Name);
        return totalCount;
    }
}
