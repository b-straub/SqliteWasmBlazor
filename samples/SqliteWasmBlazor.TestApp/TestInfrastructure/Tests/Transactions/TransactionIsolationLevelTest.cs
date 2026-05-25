using System.Data;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Transactions;

internal class TransactionIsolationLevelTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Transaction_IsolationLevel";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await AssertSupportedAsync(connection, IsolationLevel.Unspecified);
        await AssertSupportedAsync(connection, IsolationLevel.ReadUncommitted);
        await AssertSupportedAsync(connection, IsolationLevel.ReadCommitted);
        await AssertSupportedAsync(connection, IsolationLevel.RepeatableRead);
        await AssertSupportedAsync(connection, IsolationLevel.Serializable);
        await AssertSupportedAsync(connection, IsolationLevel.Snapshot);

        await AssertUnsupportedAsync(connection, IsolationLevel.Chaos);

        return "OK";
    }

    private static async Task AssertSupportedAsync(
        System.Data.Common.DbConnection connection,
        IsolationLevel isolationLevel)
    {
        await using var transaction = await connection.BeginTransactionAsync(isolationLevel);
        if (transaction.IsolationLevel != isolationLevel)
        {
            throw new InvalidOperationException(
                $"Expected isolation level {isolationLevel}, got {transaction.IsolationLevel}.");
        }

        await transaction.RollbackAsync();
    }

    private static async Task AssertUnsupportedAsync(
        System.Data.Common.DbConnection connection,
        IsolationLevel isolationLevel)
    {
        try
        {
            await using var _ = await connection.BeginTransactionAsync(isolationLevel);
        }
        catch (ArgumentException ex) when (
            ex.Message.Contains(isolationLevel.ToString(), StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException($"Expected isolation level {isolationLevel} to be rejected.");
    }
}
