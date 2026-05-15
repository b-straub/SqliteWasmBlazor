using System.Globalization;
using System.Security.Cryptography;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Services;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Bridge between the C# domain layer and the worker's encrypted bulk
/// delta export/import. The orchestrator is deliberately thin — it walks
/// the local <see cref="ColumnRegistryEntry"/> DbSet (already seeded by the
/// generator; zero reflection, no new tables) to enumerate every syncable
/// table, builds per-table WHERE clauses for a delta filter, and hands the
/// list to the worker in one call. The worker encrypts per table, signs per
/// batch, and returns one packed <see cref="DeltaEnvelope"/>.
///
/// <para>
/// The caller supplies a preconfigured <see cref="CryptoHeader"/> carrying
/// all the key material and a <c>SystemTables</c> list (from the domain app's
/// generator-emitted <c>SystemTableRegistry</c>). The orchestrator does
/// <b>not</b> look up <c>ShareGroup</c> / <c>ShareTarget</c> itself — that's
/// the caller's job, same as the existing test helpers.
/// </para>
/// </summary>
internal class SyncOrchestrator(
    ICryptoSyncDeltaService deltaService,
    CryptoSyncContextBase context,
    IImportNotifier importNotifier)
{
    /// <summary>
    /// Assemble a delta envelope of every syncable table.
    /// When <paramref name="sinceTimestamp"/> is non-null the per-table
    /// WHERE clause filters rows to <c>UpdatedAt &gt; ?</c>; null means a
    /// full snapshot. The envelope is ordered system-first so the importer
    /// can stagger permission lookups.
    /// </summary>
    /// <returns>MessagePack-packed <see cref="DeltaEnvelope"/> bytes.</returns>
    public async ValueTask<byte[]> ExportAsync(
        string databaseName,
        CryptoHeader header,
        DateTime? sinceTimestamp,
        CancellationToken cancellationToken = default)
    {
        var specs = await BuildExportSpecsAsync(header, sinceTimestamp, cancellationToken);

        var metadata = new BulkExportMetadata
        {
            Mode = sinceTimestamp is null ? 0 : 1,
            Tables = specs
        };

        var headerBytes = MessagePackSerializer.Serialize(header);
        try
        {
            return await deltaService.DeltaExportAsync(
                databaseName, metadata, headerBytes, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(headerBytes);
            header.Clear();
        }
    }

    /// <summary>
    /// Apply a delta envelope. The worker verifies the outer signature,
    /// staggers groups system-first, and runs the per-group decrypt +
    /// permission-enforce + apply pipeline for each.
    /// </summary>
    public async ValueTask<ImportReport> ImportAsync(
        string databaseName,
        CryptoHeader header,
        byte[] envelopeBytes,
        CancellationToken cancellationToken = default)
    {
        var headerBytes = MessagePackSerializer.Serialize(header);
        try
        {
            var reportBytes = await deltaService.DeltaImportAsync(
                databaseName, headerBytes, envelopeBytes, cancellationToken);
            var report = MessagePackSerializer.Deserialize<ImportReport>(reportBytes);
            await importNotifier.NotifyImportedAsync(report, cancellationToken).ConfigureAwait(false);
            return report;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(headerBytes);
            header.Clear();
        }
    }

    /// <summary>
    /// Enumerate every syncable table from the local <c>ColumnRegistry</c>
    /// (the generator-seeded schema SSOT on <see cref="CryptoSyncContextBase"/>)
    /// and build a per-table export spec with the WHERE clause for the
    /// requested delta window. System tables are ordered first so import
    /// staggering resolves permission lookups in the right order.
    /// </summary>
    private async Task<List<TableExportSpec>> BuildExportSpecsAsync(
        CryptoHeader header,
        DateTime? sinceTimestamp,
        CancellationToken cancellationToken)
    {
        var tableNames = await context.ColumnRegistry
            .Select(c => c.TableName)
            .Distinct()
            .ToListAsync(cancellationToken);

        var systemSet = new HashSet<string>(header.SystemTables, StringComparer.Ordinal);

        string? whereClause;
        IReadOnlyList<string>? whereParams;
        if (sinceTimestamp is { } since)
        {
            whereClause = "\"UpdatedAt\" > ?";
            whereParams = [since.ToString("O", CultureInfo.InvariantCulture)];
        }
        else
        {
            whereClause = null;
            whereParams = null;
        }

        return [.. tableNames
            .Select(t => new TableExportSpec
            {
                TableName = t,
                IsSystemTable = systemSet.Contains(t),
                Where = whereClause,
                WhereParams = whereParams
            })
            .OrderBy(s => s.IsSystemTable ? 0 : 1)
            .ThenBy(s => s.TableName, StringComparer.Ordinal)];
    }
}
