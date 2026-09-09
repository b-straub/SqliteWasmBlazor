using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SqliteWasmBlazor;

/// <summary>
/// One registered context's schema work.
/// </summary>
/// <remarks>
/// The generic parameter is captured here, at registration, by
/// <c>AddSqliteWasmDbContext&lt;TContext&gt;</c>. That is the whole reason this
/// type exists: the initializer runs every registered context from one
/// non-generic call, and without a closure it would need reflection to get from
/// a <see cref="Type"/> back to <c>IDbContextFactory&lt;TContext&gt;</c>.
/// </remarks>
internal sealed class DbContextSchemaDescriptor
{
    /// <summary>Runs the work, announcing through <c>onWorkStarting</c> if there is any.</summary>
    internal delegate Task<(DbInitState State, IDbInitFailure? Failure)> Step(
        IServiceProvider services,
        Func<string, ValueTask> onWorkStarting,
        CancellationToken cancellationToken);

    private DbContextSchemaDescriptor(
        Type contextType,
        Step run,
        Func<IServiceProvider, string> resolveDatabaseName)
    {
        ContextType = contextType;
        Run = run;
        ResolveDatabaseName = resolveDatabaseName;
    }

    /// <summary>The context this descriptor migrates. Used for diagnostics only.</summary>
    public Type ContextType { get; }

    /// <summary>The captured work.</summary>
    public Step Run { get; }

    /// <summary>
    /// The OPFS filename behind the context, for failures raised before the
    /// step itself runs — a worker that never started, a locked pool.
    /// </summary>
    public Func<IServiceProvider, string> ResolveDatabaseName { get; }

    /// <summary>Builds the descriptor for <typeparamref name="TContext"/>.</summary>
    public static DbContextSchemaDescriptor For<TContext>()
        where TContext : DbContext =>
        new(typeof(TContext), RunAsync<TContext>, GetDatabaseName<TContext>);

    private static async Task<(DbInitState State, IDbInitFailure? Failure)> RunAsync<TContext>(
        IServiceProvider services,
        Func<string, ValueTask> onWorkStarting,
        CancellationToken cancellationToken)
        where TContext : DbContext
    {
        // Resolved before the try so the name is available to every failure
        // payload below, including one thrown while opening the context.
        var databaseName = GetDatabaseName<TContext>(services);

        try
        {
            using var scope = services.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TContext>>();
            await using var dbContext = await factory.CreateDbContextAsync(cancellationToken);

            var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
            if (pendingMigrations.Any())
            {
                // Pending is not the same as slow. A database with nothing
                // applied yet is being *created* — every migration counts as
                // pending because none have run — and that work is a CREATE
                // TABLE against an empty file, finished before a progress bar
                // could paint. Announcing it would put MIGRATING on every first
                // run, which is how a state that means something becomes one
                // people learn to ignore.
                //
                // An upgrade is the case worth showing: rows already exist, and
                // the cost scales with how many.
                var applied = await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken);
                if (applied.Any())
                {
                    await onWorkStarting(databaseName);
                }

                try
                {
                    await dbContext.Database.MigrateAsync(cancellationToken);
                }
                catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                                           (ex.Message.Contains("table", StringComparison.OrdinalIgnoreCase) &&
                                            ex.Message.Contains("exist", StringComparison.OrdinalIgnoreCase)))
                {
                    var recovery = await MigrationHistoryRecovery.RunAsync(dbContext);

                    if (!recovery.Succeeded)
                    {
                        return (DbInitState.SCHEMA_INCOMPATIBLE,
                            new SchemaIncompatibleFailure(databaseName, recovery.Mismatches));
                    }
                }
            }

            return (DbInitState.READY, null);
        }
        catch (TimeoutException)
        {
            return (DbInitState.TIMEOUT, new TimeoutFailure(databaseName));
        }
        catch (Exception ex)
        {
            return (DbInitState.FAILED, new GenericInitFailure(databaseName, ex));
        }
    }

    /// <summary>
    /// The OPFS filename behind <typeparamref name="TContext"/>, for failure
    /// payloads. Falls back to the type name — a diagnosis naming the wrong
    /// thing still beats one that throws while being assembled.
    /// </summary>
    private static string GetDatabaseName<TContext>(IServiceProvider services)
        where TContext : DbContext
    {
        try
        {
            using var scope = services.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TContext>>();
            using var ctx = factory.CreateDbContext();
            // SqliteWasmConnection's Data Source carries the OPFS filename.
            var connectionString = ctx.Database.GetDbConnection().ConnectionString;
            return ExtractDataSource(connectionString) ?? typeof(TContext).Name;
        }
        catch
        {
            return typeof(TContext).Name;
        }
    }

    private static string? ExtractDataSource(string connectionString)
    {
        const string key = "Data Source=";
        var idx = connectionString.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var start = idx + key.Length;
        var end = connectionString.IndexOf(';', start);
        return end < 0
            ? connectionString[start..].Trim()
            : connectionString[start..end].Trim();
    }
}
