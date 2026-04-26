// SqliteWasmBlazor.CryptoSync - Boot integration with the typed status surface.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Boot-stage extensions for CryptoSync-enabled apps. Runs after
/// <c>InitializeSqliteWasmDatabaseAsync&lt;TContext&gt;</c> to verify that the
/// freshly migrated database is actually usable as a sync instance — admin
/// bootstrap completed, or member handshake completed, depending on the
/// device role declared in <see cref="DeviceSettings"/>.
/// </summary>
public static class CryptoSyncServiceCollectionExtensions
{
    /// <summary>
    /// Verifies the CryptoSync seed state of <typeparamref name="TContext"/>
    /// and reports the appropriate failure to the unified boot status. Intended
    /// for use in <c>Program.cs</c> after the library's migration helper:
    /// <code>
    /// await services.InitializeSqliteWasmDatabaseAsync&lt;TodoDbContext&gt;();
    /// await services.InitializeCryptoSyncAsync&lt;TodoDbContext&gt;();
    /// </code>
    /// Short-circuits if the prior boot stage already promoted the status away
    /// from <see cref="DbInitState.READY"/>.
    /// </summary>
    public static async ValueTask InitializeCryptoSyncAsync<TContext>(this IServiceProvider services)
        where TContext : CryptoSyncContextBase
    {
        var status = services.GetRequiredService<IDbInitializationStatus>();
        var reporter = services.GetRequiredService<IDbInitializationReporter>();

        if (status.State != DbInitState.READY)
        {
            return;
        }

        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TContext>>();
        await using var ctx = await factory.CreateDbContextAsync();

        await VerifyCryptoSyncSeedAsync(ctx, reporter, ExtractDatabaseName(ctx));
    }

    /// <summary>
    /// Runs the CryptoSync seed checks against an open context and routes the
    /// outcome through <paramref name="reporter"/>. Public so tests and apps
    /// composing custom boot pipelines can invoke the check without going
    /// through <see cref="IServiceProvider"/>.
    /// </summary>
    public static async ValueTask VerifyCryptoSyncSeedAsync(
        CryptoSyncContextBase ctx,
        IDbInitializationReporter reporter,
        string databaseName)
    {
        try
        {
            var device = await ctx.DeviceSettings.AsNoTracking().FirstOrDefaultAsync();
            if (device is null)
            {
                reporter.Report(DbInitState.FAILED, new DeviceNotProvisionedFailure(databaseName));
                return;
            }

            if (device.IsAdmin)
            {
                var hasSystemGroup = await ctx.ShareGroups.AsNoTracking()
                    .AnyAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
                if (!hasSystemGroup)
                {
                    reporter.Report(DbInitState.FAILED, new SystemSeedMissingFailure(databaseName));
                    return;
                }
            }
            else
            {
                var hasAdmin = await ctx.Contacts.AsNoTracking().AnyAsync(c => c.IsAdmin);
                if (!hasAdmin)
                {
                    reporter.Report(DbInitState.FAILED, new AdminContactMissingFailure(databaseName));
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            reporter.Report(DbInitState.FAILED, new GenericInitFailure(databaseName, ex));
        }
    }

    private static string ExtractDatabaseName(DbContext ctx)
    {
        var connectionString = ctx.Database.GetDbConnection().ConnectionString;
        const string key = "Data Source=";
        var idx = connectionString.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return ctx.GetType().Name;
        }

        var start = idx + key.Length;
        var end = connectionString.IndexOf(';', start);
        return end < 0
            ? connectionString[start..].Trim()
            : connectionString[start..end].Trim();
    }
}
