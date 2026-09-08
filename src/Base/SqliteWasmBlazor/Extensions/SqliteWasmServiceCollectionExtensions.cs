// SqliteWasmBlazor - Minimal EF Core compatible provider
// MIT License

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SqliteWasmBlazor;

/// <summary>
/// Extension methods for configuring SqliteWasm services.
/// </summary>
public static class SqliteWasmServiceCollectionExtensions
{
    /// <summary>
    /// Registers SqliteWasm services and configuration. Call this in Program.cs before
    /// <c>WebAssemblyHostBuilder.Build()</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration callback. For sub-path deployments
    /// set <see cref="Hosting.SqliteWasmAssetOptions.BaseHref"/> (e.g.
    /// <c>new Uri(builder.HostEnvironment.BaseAddress).AbsolutePath</c>); for
    /// browser-extension builds override <see cref="Hosting.SqliteWasmAssetOptions.AssetRoot"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSqliteWasm(
        this IServiceCollection services,
        Action<SqliteWasmOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<SqliteWasmOptions>();
        }

        services.AddSingleton<ISqliteWasmDatabaseService>(SqliteWasmWorkerBridge.Instance);

        // Encrypted-disk lifecycle (IEncryptedSqliteWasmDatabaseService) is
        // registered by AddSqliteWasmBlazorCrypto — it depends on IPrfService
        // for the implicit PRF cache cleanup on ResetPool. Plain-only
        // consumers (AdoNetSample) only call AddSqliteWasm and never see
        // the encrypted-disk surface.
        services.AddSingleton<DbInitializationService>();
        services.AddSingleton<IDbInitializationStatus>(sp => sp.GetRequiredService<DbInitializationService>());
        services.AddSingleton<IDbInitializationReporter>(sp => sp.GetRequiredService<DbInitializationService>());

        // Resolved through the interfaces above rather than the concrete
        // service: Crypto.UI replaces both with DbStateModel, and the schema
        // step must report through whichever is registered.
        services.AddSingleton<DbSchemaInitializer>();
        services.AddSingleton<IDbSchemaInitializer>(sp => sp.GetRequiredService<DbSchemaInitializer>());

        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="THost"/> as the host seam the import
    /// paths consult — which databases this app owns, and whether what an
    /// import brought is a valid one of them.
    ///
    /// <para>
    /// One class, one call. Consumers of <c>SqliteWasmBlazor.Crypto.UI</c>
    /// use its <c>AddHostRecoveryService</c> instead, which binds the same
    /// instance to the recovery interface those panels resolve as well.
    /// </para>
    /// </summary>
    /// <typeparam name="THost">The host's implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHostDatabaseService<THost>(
        this IServiceCollection services)
        where THost : class, IHostDatabaseService
    {
        services.AddScoped<THost>();
        services.AddScoped<IHostDatabaseService>(sp => sp.GetRequiredService<THost>());
        return services;
    }

    /// <summary>
    /// Initializes the SqliteWasm worker bridge using the options configured via
    /// <see cref="AddSqliteWasm"/>. Use this method when consuming the ADO.NET provider
    /// directly without EF Core.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when initialization fails or database is locked by another tab.</exception>
    public static async Task InitializeSqliteWasmAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<IOptions<SqliteWasmOptions>>().Value;
        var reporter = services.GetRequiredService<IDbInitializationReporter>();
        ConfigureCommandLogging(options);

        SqliteWasmWorkerBridge.Instance.AttachBootStatus(
            reporter, services.GetRequiredService<IDbInitializationStatus>());
        SqliteWasmWorkerBridge.Instance.AttachHostDatabaseService(
            services.GetService<IHostDatabaseService>);
        reporter.Report(DbInitState.INITIALIZING);

        try
        {
            await SqliteWasmWorkerBridge.Instance.InitializeAsync(options, cancellationToken);
            reporter.Report(DbInitState.READY);
        }
        catch (Exception ex)
        {
            reporter.Report(DbInitState.TAB_LOCKED, new TabLockedFailure(string.Empty));
            throw new InvalidOperationException(
$"""
{ex.Message}
Database is locked by another browser tab.
This application uses OPFS (Origin Private File System) which only allows one tab to access the database at a time.
Please close any other tabs running this application and refresh the page.
""", ex);
        }
    }

    /// <summary>
    /// Initializes the SqliteWasm worker bridge and applies pending EF Core migrations
    /// for <typeparamref name="TContext"/>, recovering the migration history when necessary.
    /// Boot outcome is reported to <see cref="IDbInitializationReporter"/>; consumers
    /// observe via <see cref="IDbInitializationStatus"/>.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type to initialize.</typeparam>
    /// <param name="services">The service provider.</param>
    public static async Task InitializeSqliteWasmDatabaseAsync<TContext>(
        this IServiceProvider services)
        where TContext : DbContext
    {
        var reporter = services.GetRequiredService<IDbInitializationReporter>();
        var status = services.GetRequiredService<IDbInitializationStatus>();
        var options = services.GetRequiredService<IOptions<SqliteWasmOptions>>().Value;
        ConfigureCommandLogging(options);

        // A whole-pool import reports READY through this so every
        // <AuthorizeView> bound to the state re-evaluates; the bridge is a
        // singleton outside the container and cannot resolve it itself.
        SqliteWasmWorkerBridge.Instance.AttachBootStatus(reporter, status);

        // Every import reconciles the host's schema with what it landed, so
        // the invariant holds for a headless consumer too. Resolved per call
        // rather than captured — the seam is Scoped, the bridge is not.
        SqliteWasmWorkerBridge.Instance.AttachHostDatabaseService(
            services.GetService<IHostDatabaseService>);

        // Skip if a previous boot stage already failed — don't overwrite that diagnosis.
        if (status.State is DbInitState.TAB_LOCKED
                          or DbInitState.SCHEMA_INCOMPATIBLE
                          or DbInitState.TIMEOUT
                          or DbInitState.FAILED
                          or DbInitState.ENCRYPTED_LOCKED)
        {
            return;
        }

        reporter.Report(DbInitState.INITIALIZING);

        try
        {
            await SqliteWasmWorkerBridge.Instance.InitializeAsync(options);
        }
        catch (Exception ex)
        {
            reporter.Report(DbInitState.TAB_LOCKED, new TabLockedFailure(GetDatabaseName<TContext>(services, ex)));
            return;
        }

        var databaseName = GetDatabaseName<TContext>(services, null);

        // Encrypted-VFS probe: ask plane 2 for its state via the plane-1-facing
        // IDatabaseLockProbe (implemented by EncryptedSqliteWasmDatabaseService).
        // The probe self-heals an orphan hint (localStorage marker without any
        // ciphertext on disk — left over from an aborted EnterEncrypted
        // ceremony) by clearing the hint and returning Plain. If the state is
        // genuinely Encrypted+Locked, report ENCRYPTED_LOCKED with the hint so
        // the UI can prompt for credentials; the user calls
        // IEncryptedSqliteWasmDatabaseService.UnlockAsync(key) (the lifecycle
        // service does it automatically once the auth flow completes) and the
        // policy gate flips on its own. Resolved optionally — plain-only
        // consumers that didn't call AddSqliteWasmBlazorCrypto() skip it.
        var probe = services.GetService<IDatabaseLockProbe>();
        if (probe is not null)
        {
            var lockState = await probe.GetStateAsync();
            if (lockState.Encrypted && !lockState.Unlocked)
            {
                reporter.Report(
                    DbInitState.ENCRYPTED_LOCKED,
                    new EncryptedDatabaseLockedFailure(databaseName, lockState.Hint));
                return;
            }
        }

        // The schema work is registered, not run. It needs a database that can
        // be opened, and for an encrypted pool that is only true after a key
        // arrives — which cannot happen before the app renders. Running it here
        // is what left encrypted pools never migrating at all. See
        // IDbSchemaInitializer.
        RegisterSchemaStep<TContext>(services, databaseName);

        // Driving initialization again means the caller believes something has
        // changed — a retry after a failure, or a test staging a broken schema.
        // The previous outcome is not evidence about the database as it is now.
        services.GetRequiredService<IDbSchemaInitializer>().Reset();

        // A plain pool is openable the moment the worker is up, so nothing has
        // to wait for it — but the step still runs from the same place, so
        // there is one migration site rather than two.
        if (probe is null || !(await probe.GetStateAsync()).Encrypted)
        {
            await services.GetRequiredService<IDbSchemaInitializer>().EnsureSchemaAsync();
        }
    }

    /// <summary>
    /// Adds the pending-migration + history-recovery work for
    /// <typeparamref name="TContext"/> to the deferred schema step.
    /// </summary>
    private static void RegisterSchemaStep<TContext>(IServiceProvider services, string databaseName)
        where TContext : DbContext
    {
        var initializer = (DbSchemaInitializer)services.GetRequiredService<IDbSchemaInitializer>();

        initializer.Register(typeof(TContext), async (onWorkStarting, cancellationToken) =>
        {
            try
            {
                using var scope = services.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TContext>>();
                await using var dbContext = await factory.CreateDbContextAsync(cancellationToken);

                var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
                if (pendingMigrations.Any())
                {
                    // Only now is there something worth telling the user about.
                    await onWorkStarting();

                    try
                    {
                        await dbContext.Database.MigrateAsync(cancellationToken);
                    }
                    catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                                                (ex.Message.Contains("table", StringComparison.OrdinalIgnoreCase) &&
                                                 ex.Message.Contains("exist", StringComparison.OrdinalIgnoreCase)))
                    {
                        var recovery = await RecoverMigrationHistoryAsync(dbContext);

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
        });
    }

    private static void ConfigureCommandLogging(SqliteWasmOptions options)
    {
        SqliteWasmLogger.CommandSqlLoggingEnabled = options.EnableCommandSqlLogging;
        SqliteWasmLogger.TracingEnabled = options.EnableRequestTracing;

        // Says so once, so a benchmarking session can tell at a glance that the
        // numbers below are actually being produced.
        SqliteWasmLogger.Trace(nameof(SqliteWasmLogger), "request tracing enabled");
    }

    private static string GetDatabaseName<TContext>(IServiceProvider services, Exception? _)
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

    /// <summary>
    /// Outcome of <see cref="RecoverMigrationHistoryAsync"/>: whether recovery
    /// landed on a usable schema, and any per-column mismatches detected
    /// during verification.
    /// </summary>
    private sealed record RecoveryResult(bool Succeeded, IReadOnlyList<SchemaMismatch> Mismatches);

    /// <summary>
    /// Recovers the migration history table when it's missing or corrupted.
    /// Walks every entity in the EF model and verifies its mapped columns
    /// exist in the live SQLite schema. Returns structured per-column
    /// diagnostics so callers can render actionable UI rather than a string.
    /// </summary>
    private static async Task<RecoveryResult> RecoverMigrationHistoryAsync(DbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        var mismatches = new List<SchemaMismatch>();

        try
        {
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (
                    MigrationId TEXT NOT NULL PRIMARY KEY,
                    ProductVersion TEXT NOT NULL
                );";
            await cmd.ExecuteNonQueryAsync();

            var allMigrations = dbContext.Database.GetMigrations();
            foreach (var migration in allMigrations)
            {
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                    VALUES ($migration, $version);";
                cmd.Parameters.Clear();

                var migrationParam = cmd.CreateParameter();
                migrationParam.ParameterName = "$migration";
                migrationParam.Value = migration;
                cmd.Parameters.Add(migrationParam);

                var versionParam = cmd.CreateParameter();
                versionParam.ParameterName = "$version";
                versionParam.Value = "10.0.0";
                cmd.Parameters.Add(versionParam);

                await cmd.ExecuteNonQueryAsync();
            }

            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name != '__EFMigrationsHistory';";
            cmd.Parameters.Clear();
            var tableCount = await cmd.ExecuteScalarAsync();

            if (tableCount is null || Convert.ToInt64(tableCount) == 0)
            {
                return new RecoveryResult(false, mismatches);
            }

            // Use the design-time model so IsTableExcludedFromMigrations is
            // available — the runtime model strips that annotation. Mirrors
            // ValidateImportedSchemaAsync's filter so FTS5 / virtual tables
            // marked ExcludeFromMigrations don't produce spurious mismatches.
            var designTimeModel = dbContext.GetService<IDesignTimeModel>().Model;
            foreach (var entityType in designTimeModel.GetEntityTypes())
            {
                var tableName = entityType.GetTableName();
                if (string.IsNullOrEmpty(tableName)
                    || entityType.IsOwned()
                    || entityType.IsTableExcludedFromMigrations())
                {
                    continue;
                }

                // PRAGMA table_info is the SQLite-canonical introspection
                // path. SELECT * LIMIT 0 is unreliable here — some drivers
                // (this one included) only populate column metadata when at
                // least one row is materialized, leaving FieldCount=0 on
                // empty results and miscounting every column as missing.
                cmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";
                cmd.Parameters.Clear();

                var actualColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    // table_info row shape: cid, name, type, notnull, dflt_value, pk
                    while (await reader.ReadAsync())
                    {
                        actualColumns.Add(reader.GetString(1));
                    }
                }

                var expectedColumns = entityType.GetProperties()
                    .Where(p => !p.IsShadowProperty())
                    .Select(p => p.GetColumnName())
                    .Where(c => !string.IsNullOrEmpty(c))
                    .Select(c => c)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var expectedColumn in expectedColumns)
                {
                    if (!actualColumns.Contains(expectedColumn))
                    {
                        mismatches.Add(new SchemaMismatch(tableName, expectedColumn, null));
                    }
                }

                foreach (var actualColumn in actualColumns)
                {
                    if (!expectedColumns.Contains(actualColumn))
                    {
                        mismatches.Add(new SchemaMismatch(tableName, null, actualColumn));
                    }
                }
            }

            return new RecoveryResult(mismatches.Count == 0, mismatches);
        }
        catch
        {
            return new RecoveryResult(false, mismatches);
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                await connection.CloseAsync();
            }
        }
    }
}
