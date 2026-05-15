// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Sqlite.Migrations.Internal;

namespace SqliteWasmBlazor;

/// <summary>
/// Custom history repository for SqliteWasm that disables migration locking.
/// The migration lock mechanism causes infinite polling loops in WebAssembly
/// because there's no concurrent access in a single-user browser environment.
///
/// NOTE: Inherits from SqliteHistoryRepository (internal API - EF1001 warning suppressed).
/// This is necessary as EF Core provides no stable configuration option to disable migration locking.
/// The implementation is stable for .NET 10 and will not change.
/// Safe for single-user browser environment where OPFS access is already synchronized via Web Worker.
/// </summary>
#pragma warning disable EF1001
internal sealed class SqliteWasmHistoryRepository : SqliteHistoryRepository
{
    public SqliteWasmHistoryRepository(HistoryRepositoryDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <summary>
    /// Override to disable migration locking for WASM environment.
    /// Browser environment is single-user and OPFS access is already single-threaded via Web Worker.
    /// The base implementation has an infinite while(true) loop with Task.Delay that doesn't work in WASM.
    /// </summary>
    public override async Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(
        CancellationToken cancellationToken = default)
    {
        // Skip locking entirely - just return a no-op lock
        // Single-user browser environment doesn't need concurrent migration protection
        await Task.CompletedTask; // Avoid async warning
        return new NoOpMigrationsDatabaseLock(this);
    }

    /// <summary>
    /// Override to disable migration locking for WASM environment (synchronous version).
    /// </summary>
    public override IMigrationsDatabaseLock AcquireDatabaseLock()
    {
        return new NoOpMigrationsDatabaseLock(this);
    }

    /// <summary>
    /// No-op migration lock implementation that does nothing on disposal.
    /// </summary>
    private sealed class NoOpMigrationsDatabaseLock : IMigrationsDatabaseLock
    {
        private readonly IHistoryRepository _historyRepository;

        public NoOpMigrationsDatabaseLock(IHistoryRepository historyRepository)
        {
            _historyRepository = historyRepository;
        }

        IHistoryRepository IMigrationsDatabaseLock.HistoryRepository => _historyRepository;
        
        public void Dispose()
        {
            // No-op: nothing to release
        }

        public ValueTask DisposeAsync()
        {
            // No-op: nothing to release
            return ValueTask.CompletedTask;
        }
    }
}
#pragma warning restore EF1001