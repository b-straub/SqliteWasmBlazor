using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class NativePragmaSettingsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_NativePragmaSettings";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        AssertEqual(1L, await ExecuteScalarAsync(connection, "PRAGMA foreign_keys = ON; PRAGMA foreign_keys;"), "PRAGMA foreign_keys");
        AssertEqual(1L, await ExecuteScalarAsync(connection, "PRAGMA recursive_triggers = ON; PRAGMA recursive_triggers;"), "PRAGMA recursive_triggers");
        AssertEqual(12345L, await ExecuteScalarAsync(connection, "PRAGMA user_version = 12345; PRAGMA user_version;"), "PRAGMA user_version");
        AssertEqual(11259375L, await ExecuteScalarAsync(connection, "PRAGMA application_id = 11259375; PRAGMA application_id;"), "PRAGMA application_id");
        AssertEqual(-2048L, await ExecuteScalarAsync(connection, "PRAGMA cache_size = -2048; PRAGMA cache_size;"), "PRAGMA cache_size");
        AssertEqual(2500L, await ExecuteScalarAsync(connection, "PRAGMA busy_timeout = 2500; PRAGMA busy_timeout;"), "PRAGMA busy_timeout");
        AssertEqual(1L, await ExecuteScalarAsync(connection, "PRAGMA synchronous = NORMAL; PRAGMA synchronous;"), "PRAGMA synchronous");
        AssertEqual(2L, await ExecuteScalarAsync(connection, "PRAGMA temp_store = MEMORY; PRAGMA temp_store;"), "PRAGMA temp_store");

        var journalMode = Convert.ToString(
            await ExecuteScalarAsync(connection, "PRAGMA journal_mode = MEMORY;"),
            System.Globalization.CultureInfo.InvariantCulture);
        if (journalMode is not "memory" and not "wal")
        {
            throw new InvalidOperationException($"PRAGMA journal_mode returned {journalMode}; expected memory or wal.");
        }

        var pageSize = Convert.ToInt64(await ExecuteScalarAsync(connection, "PRAGMA page_size;"));
        if (pageSize <= 0)
        {
            throw new InvalidOperationException($"PRAGMA page_size returned {pageSize}; expected positive page size.");
        }

        AssertEqual("UTF-8", await ExecuteScalarAsync(connection, "PRAGMA encoding;"), "PRAGMA encoding");
        AssertEqual("ok", await ExecuteScalarAsync(connection, "PRAGMA integrity_check;"), "PRAGMA integrity_check");
        AssertEqual("ok", await ExecuteScalarAsync(connection, "PRAGMA quick_check;"), "PRAGMA quick_check");

        return "OK";
    }

    private static async Task<object?> ExecuteScalarAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static void AssertEqual<T>(T expected, object? actual, string pragmaName)
    {
        if (actual is null)
        {
            throw new InvalidOperationException($"{pragmaName} returned null; expected {expected}.");
        }

        var converted = typeof(T) == typeof(string)
            ? (T)(object)Convert.ToString(actual, System.Globalization.CultureInfo.InvariantCulture)!
            : (T)Convert.ChangeType(actual, typeof(T), System.Globalization.CultureInfo.InvariantCulture);

        if (!EqualityComparer<T>.Default.Equals(expected, converted))
        {
            throw new InvalidOperationException(
                $"{pragmaName} returned {converted}; expected {expected}.");
        }
    }
}
