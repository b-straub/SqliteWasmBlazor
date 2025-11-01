using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SQLiteNET.Opfs.Abstractions;

namespace SQLiteNET.Opfs.Interceptors;

/// <summary>
/// Interceptor for EF Core that automatically persists database changes to OPFS.
/// Monitors SQL commands for write operations (INSERT, UPDATE, DELETE, etc.) and triggers
/// throttled persistence to avoid excessive file writes.
/// </summary>
public sealed class OpfsDbContextInterceptor : IDbCommandInterceptor
{
    private readonly IOpfsStorage _storage;
    private readonly Dictionary<string, CancellationTokenSource> _throttledSyncTasks = new();
    private readonly TimeSpan _throttleDelay = TimeSpan.FromMilliseconds(50);

    public OpfsDbContextInterceptor(IOpfsStorage storage)
    {
        _storage = storage;
    }

    public ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (IsTargetedCommand(command.CommandText))
        {
            var dataSource = eventData.Context?.Database.GetDbConnection().DataSource;
            if (dataSource is not null)
            {
                _ = ThrottledSync(dataSource);
            }
        }

        return ValueTask.FromResult(result);
    }

    public ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        if (IsTargetedCommand(command.CommandText))
        {
            var dataSource = eventData.Context?.Database.GetDbConnection().DataSource;
            if (dataSource is not null)
            {
                _ = ThrottledSync(dataSource);
            }
        }

        return ValueTask.FromResult(result);
    }

    public ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (IsTargetedCommand(command.CommandText))
        {
            var dataSource = eventData.Context?.Database.GetDbConnection().DataSource;
            if (dataSource is not null)
            {
                _ = ThrottledSync(dataSource);
            }
        }

        return ValueTask.FromResult(result);
    }

    private async Task ThrottledSync(string dataSource)
    {
        var fileName = Path.GetFileName(dataSource);

        if (_throttledSyncTasks.TryGetValue(fileName, out var existingCts))
        {
            // Cancel existing task and create new one
            existingCts.Cancel();
            _throttledSyncTasks.Remove(fileName);
        }

        var cts = new CancellationTokenSource();
        _throttledSyncTasks[fileName] = cts;

        try
        {
            await Task.Delay(_throttleDelay, cts.Token);

            // Only persist if not cancelled
            if (!cts.Token.IsCancellationRequested)
            {
                await _storage.Persist(fileName);
                _throttledSyncTasks.Remove(fileName);
            }
        }
        catch (TaskCanceledException)
        {
            // Expected when throttling
        }
    }

    private static bool IsTargetedCommand(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return false;
        }

        var normalizedCommand = commandText.Trim();

        return normalizedCommand.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.StartsWith("DROP", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.StartsWith("ALTER", StringComparison.OrdinalIgnoreCase) ||
               normalizedCommand.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase);
    }

    #region Unused Interface Members

    public int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        => result;

    public object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
        => result;

    public DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        => result;

    public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        => result;

    public InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        => result;

    public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        => result;

    public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(result);

    public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(result);

    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(result);

    public void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
    }

    public Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public DbCommand CommandCreated(CommandEndEventData eventData, DbCommand result)
        => result;

    public InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        => result;

    public InterceptionResult DataReaderClosing(DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        => result;

    public ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        => ValueTask.FromResult(result);

    #endregion
}
