using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Transactions;

internal class TransactionCommitTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Transaction_Commit";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        await using var transaction = await context.Database.BeginTransactionAsync();

        var item = new TodoItem
        {
            Title = "Transaction Test",
            Description = "Test",
            CreatedAt = DateTime.UtcNow
        };

        context.TodoItems.Add(item);
        await context.SaveChangesAsync();

        await transaction.CommitAsync();

        var found = await context.TodoItems.FindAsync(item.Id);
        if (found is null)
        {
            throw new InvalidOperationException("Transaction commit failed");
        }

        return "OK";
    }
}
