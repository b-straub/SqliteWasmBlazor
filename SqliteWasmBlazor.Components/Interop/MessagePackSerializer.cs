using MessagePack;
using Microsoft.Extensions.Logging;

namespace SqliteWasmBlazor.Components.Interop;

/// <summary>
/// Generic streaming serialization helper for entity collections using MessagePack V2 format.
/// V2 header is self-describing with column metadata for worker-side bulk operations.
/// </summary>
public static class MessagePackSerializer<T>
{
    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard;

    /// <summary>
    /// Serialize entities to a stream as V2 format (header + items).
    /// Header contains column metadata for worker-side SQL construction and type conversions.
    /// </summary>
    public static async Task SerializeStreamAsync(
        IEnumerable<T> items,
        Stream stream,
        string tableName,
        string primaryKeyColumn,
        int mode = 0,
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

        logger?.LogDebug("Starting V2 serialization of {Count} {Type} items", total, typeof(T).Name);

        var header = MessagePackFileHeaderV2.Create<T>(tableName, primaryKeyColumn, total, mode, appIdentifier);
        logger?.LogDebug("Writing V2 header: Type={Type}, SchemaHash={Hash}, Records={Count}, Table={Table}",
            header.DataType, header.SchemaHash, header.RecordCount, header.TableName);

        await MessagePackSerializer.SerializeAsync(stream, header, Options, cancellationToken);

        foreach (var item in itemList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await MessagePackSerializer.SerializeAsync(stream, item, Options, cancellationToken);

            current++;
            progress?.Report((current, total));
        }

        logger?.LogInformation("Serialized {Count} {Type} items to {Bytes} bytes (V2)",
            total, typeof(T).Name, stream.Length);
    }

    /// <summary>
    /// Deserialize entities from a V2 stream (header + items).
    /// Validates V2 header for schema compatibility, then reads items in batches.
    /// </summary>
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

        logger?.LogDebug("Starting V2 deserialization with batch size {BatchSize}", batchSize);
        var streamReader = new MessagePackStreamReader(stream);

        // Read and validate V2 header
        var headerData = await streamReader.ReadAsync(cancellationToken);
        if (headerData is null)
        {
            throw new InvalidOperationException("File is empty or missing header");
        }

        MessagePackFileHeaderV2 header;
        try
        {
            var headerSequence = headerData.Value;
            header = MessagePackSerializer.Deserialize<MessagePackFileHeaderV2>(in headerSequence, Options, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Invalid or missing V2 file header. This file may not be a valid export from this application.", ex);
        }

        logger?.LogDebug("Read V2 header: Type={Type}, SchemaHash={Hash}, Records={Count}, Table={Table}",
            header.DataType, header.SchemaHash, header.RecordCount, header.TableName);

        var expectedType = typeof(T).FullName ?? typeof(T).Name;
        header.Validate(expectedType, expectedSchemaHash, expectedAppIdentifier);

        logger?.LogInformation("Importing {Count} {Type} records (schema hash {Hash}, table {Table})",
            header.RecordCount, typeof(T).Name, header.SchemaHash, header.TableName);

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

        logger?.LogInformation("Deserialized {Count} {Type} items (V2)", totalCount, typeof(T).Name);
        return totalCount;
    }
}
