using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Transactions;

internal class TransactionRollbackTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Transaction_Rollback";

    public override async ValueTask<string?> RunTestAsync()
    {
        int initialCount;

        // Get initial count
        await using (var context = await Factory.CreateDbContextAsync())
        {
            initialCount = await context.TodoItems.CountAsync();
        }

        // Try to add item in transaction and rollback
        await using (var context = await Factory.CreateDbContextAsync())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();

            var item = new TodoItem
            {
                Title = "Rollback Test",
                Description = "Test",
                CreatedAt = DateTime.UtcNow
            };

            context.TodoItems.Add(item);
            await context.SaveChangesAsync();

            await transaction.RollbackAsync();
        }

        // Verify count in fresh context
        await using (var verifyContext = await Factory.CreateDbContextAsync())
        {
            var finalCount = await verifyContext.TodoItems.CountAsync();
            if (finalCount != initialCount)
            {
                throw new InvalidOperationException($"Transaction rollback failed: expected {initialCount}, got {finalCount}");
            }
        }

        return "OK";
    }
}
