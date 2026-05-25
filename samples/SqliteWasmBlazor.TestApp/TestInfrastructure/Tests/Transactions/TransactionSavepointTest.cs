using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Transactions;

internal class TransactionSavepointTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Transaction_Savepoint";

    public override async ValueTask<string?> RunTestAsync()
    {
        var beforeSavepointId = Guid.NewGuid();
        var rolledBackId = Guid.NewGuid();
        var afterRollbackId = Guid.NewGuid();

        await using (var context = await Factory.CreateDbContextAsync())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();

            context.TodoItems.Add(CreateItem(beforeSavepointId, "Before savepoint"));
            await context.SaveChangesAsync();

            await transaction.CreateSavepointAsync("after_first_insert");

            context.TodoItems.Add(CreateItem(rolledBackId, "Rolled back"));
            await context.SaveChangesAsync();

            await transaction.RollbackToSavepointAsync("after_first_insert");

            context.ChangeTracker.Clear();
            context.TodoItems.Add(CreateItem(afterRollbackId, "After rollback"));
            await context.SaveChangesAsync();

            await transaction.ReleaseSavepointAsync("after_first_insert");
            await transaction.CommitAsync();
        }

        await using var verifyContext = await Factory.CreateDbContextAsync();
        var persistedIds = await verifyContext.TodoItems
            .Where(item => item.Id == beforeSavepointId || item.Id == rolledBackId || item.Id == afterRollbackId)
            .OrderBy(item => item.Title)
            .Select(item => item.Id)
            .ToListAsync();

        if (!persistedIds.SequenceEqual(new[] { afterRollbackId, beforeSavepointId }))
        {
            throw new InvalidOperationException(
                $"Savepoint rollback persisted unexpected rows [{string.Join(",", persistedIds)}].");
        }

        return "OK";
    }

    private static TodoItem CreateItem(Guid id, string title)
    {
        return new TodoItem
        {
            Id = id,
            Title = title,
            Description = "Transaction savepoint compatibility",
            UpdatedAt = DateTime.UtcNow
        };
    }
}
