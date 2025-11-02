using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SQLiteNET.Opfs.Abstractions;

namespace SQLiteNET.Opfs.Factories;

/// <summary>
/// Pooled DbContext factory with OPFS integration.
/// Automatically loads the database from OPFS on first access.
/// </summary>
/// <typeparam name="TDbContext">The DbContext type</typeparam>
public class OpfsPooledDbContextFactory<TDbContext> : PooledDbContextFactory<TDbContext>
    where TDbContext : DbContext
{
    private readonly string _fileName;
    private readonly IOpfsStorage _storage;
    private readonly Func<IServiceProvider, TDbContext, ValueTask>? _dbContextInitializer;
    private readonly TaskCompletionSource _initializationTcs = new();
    private bool _isInitialized;

    public OpfsPooledDbContextFactory(
        IOpfsStorage storage,
        DbContextOptions<TDbContext> options,
        Func<IServiceProvider, TDbContext, ValueTask>? dbContextInitializer = null)
        : base(options)
    {
        var connectionString = options.Extensions
            .OfType<RelationalOptionsExtension>()
            .FirstOrDefault(r => !string.IsNullOrEmpty(r.ConnectionString))?.ConnectionString;

        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("Connection string not found in DbContext options");
        }

        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };

        _fileName = builder["Data Source"].ToString()!.Trim('/');
        _storage = storage;
        _dbContextInitializer = dbContextInitializer;
    }

    public override async Task<TDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            await InitializeAsync();
        }

        await _initializationTcs.Task;
        return await base.CreateDbContextAsync(cancellationToken);
    }

    public override TDbContext CreateDbContext()
    {
        throw new NotSupportedException(
            $"{nameof(CreateDbContext)} is not supported with OPFS. " +
            $"Use {nameof(CreateDbContextAsync)} instead for proper async initialization.");
    }

    private async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;

        try
        {
            // Check if database file exists in Emscripten MEMFS
            if (!File.Exists(_fileName))
            {
                // Load from OPFS if exists
                try
                {
                    await _storage.Load(_fileName);
                }
                catch
                {
                    // Database doesn't exist in OPFS yet - will be created
                }
            }

            // Run custom initialization if provided
            if (_dbContextInitializer is not null)
            {
                await using var dbContext = await base.CreateDbContextAsync();
                await _dbContextInitializer(dbContext.GetService<IServiceProvider>(), dbContext);
            }

            _initializationTcs.SetResult();
        }
        catch (Exception ex)
        {
            _initializationTcs.SetException(ex);
            throw;
        }
    }
}
