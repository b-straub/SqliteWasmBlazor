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

        services.AddSingleton<DbInitializationService>();
        services.AddSingleton<IDbInitializationStatus>(sp => sp.GetRequiredService<DbInitializationService>());
        services.AddSingleton<IDbInitializationReporter>(sp => sp.GetRequiredService<DbInitializationService>());

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

        try
        {
            using var scope = services.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TContext>>();
            await using var dbContext = await factory.CreateDbContextAsync();

            var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
            if (pendingMigrations.Any())
            {
                try
                {
                    await dbContext.Database.MigrateAsync();
                }
                catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                                            (ex.Message.Contains("table", StringComparison.OrdinalIgnoreCase) &&
                                             ex.Message.Contains("exist", StringComparison.OrdinalIgnoreCase)))
                {
                    var recovery = await RecoverMigrationHistoryAsync(dbContext);

                    if (!recovery.Succeeded)
                    {
                        reporter.Report(
                            DbInitState.SCHEMA_INCOMPATIBLE,
                            new SchemaIncompatibleFailure(databaseName, recovery.Mismatches));
                        return;
                    }
                }
            }

            reporter.Report(DbInitState.READY);
        }
        catch (TimeoutException)
        {
            reporter.Report(DbInitState.TIMEOUT, new TimeoutFailure(databaseName));
        }
        catch (Exception ex) when (LooksLikeEncryptedLockedDb(ex))
        {
            // EF Core attempted a page read on an encrypted file with no
            // worker-side key registered → SQLITE_NOTADB. Not an error the
            // user can resolve via reset; the cure is sign-in. The
            // AuthenticationPanel install path will promote the state to
            // READY once K is registered and a successful read confirms it.
            reporter.Report(
                DbInitState.ENCRYPTED_LOCKED,
                new EncryptedDatabaseLockedFailure(databaseName));
        }
        catch (Exception ex)
        {
            reporter.Report(DbInitState.FAILED, new GenericInitFailure(databaseName, ex));
        }
    }

    /// <summary>
    /// Heuristic: did this exception come from EF Core trying to read schema
    /// off an encrypted file the worker has no key for? Worker surfaces this
    /// as <c>SQLITE_NOTADB</c> (sqlite3 result code 26) wrapped in a
    /// "Worker error" prefix before bubbling through EF.
    /// </summary>
    private static bool LooksLikeEncryptedLockedDb(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e.Message.Contains("SQLITE_NOTADB", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("file is not a database", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("result code 26", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (e.InnerException is null)
            {
                return false;
            }
        }
        return false;
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
                    .Select(c => c!)
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
