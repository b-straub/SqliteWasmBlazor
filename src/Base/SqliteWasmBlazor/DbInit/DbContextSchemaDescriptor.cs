using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
                try
                {
                    await MigrateAndAnnounceAsync(dbContext, databaseName, onWorkStarting, cancellationToken);
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
    /// Runs the migration, announcing it only if it is still going after
    /// <see cref="SqliteWasmOptions.MigrationAnnounceDelay"/>.
    /// </summary>
    /// <remarks>
    /// Nothing available before the work starts says whether it will be slow.
    /// "Are migrations pending?" is true on every first run — a database with
    /// none applied has all of them pending — and "has anything been applied
    /// already?" is no better a proxy. So this does not predict: it starts the
    /// work and announces only if it is still running when the window elapses.
    /// An empty database is created in silence; an upgrade over real rows shows
    /// a progress state for exactly as long as it takes.
    /// </remarks>
    private static async Task MigrateAndAnnounceAsync(
        DbContext dbContext,
        string databaseName,
        Func<string, ValueTask> onWorkStarting,
        CancellationToken cancellationToken)
    {
        var delay = dbContext.GetService<IOptions<SqliteWasmOptions>>().Value.MigrationAnnounceDelay;
        var migrating = dbContext.Database.MigrateAsync(cancellationToken);

        if (delay > TimeSpan.Zero)
        {
            using var announceWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var window = Task.Delay(delay, announceWindow.Token);

            if (await Task.WhenAny(migrating, window) == migrating)
            {
                // Done inside the window — nobody needed telling.
                await announceWindow.CancelAsync();
                await migrating;
                return;
            }
        }

        await onWorkStarting(databaseName);
        await migrating;
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
