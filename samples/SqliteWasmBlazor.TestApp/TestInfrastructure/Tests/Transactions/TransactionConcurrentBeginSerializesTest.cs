using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Transactions;

internal class TransactionConcurrentBeginSerializesTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Transaction_ConcurrentBeginSerializes";

    public override async ValueTask<string?> RunTestAsync()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        await using var firstContext = await Factory.CreateDbContextAsync();
        await using var firstTransaction = await firstContext.Database.BeginTransactionAsync();

        firstContext.TodoItems.Add(CreateItem(firstId, "First transaction"));
        await firstContext.SaveChangesAsync();

        var secondTask = RunSecondTransactionAsync(secondId);
        var completedBeforeFirstCommit = await Task.WhenAny(secondTask, Task.Delay(250)) == secondTask;
        if (completedBeforeFirstCommit)
        {
            await secondTask;
            throw new InvalidOperationException("Second transaction completed before the first transaction committed.");
        }

        await firstTransaction.CommitAsync();
        await secondTask;

        await using var verifyContext = await Factory.CreateDbContextAsync();
        var persistedCount = await verifyContext.TodoItems
            .CountAsync(item => item.Id == firstId || item.Id == secondId);
        if (persistedCount != 2)
        {
            throw new InvalidOperationException($"Expected both transaction rows to persist, got {persistedCount}.");
        }

        return "OK";
    }

    private async Task RunSecondTransactionAsync(Guid itemId)
    {
        await using var secondContext = CreateIndependentContext();
        await using var secondTransaction = await secondContext.Database.BeginTransactionAsync();

        secondContext.TodoItems.Add(CreateItem(itemId, "Second transaction"));
        await secondContext.SaveChangesAsync();
        await secondTransaction.CommitAsync();
    }

    private static TodoDbContext CreateIndependentContext()
    {
        var options = new DbContextOptionsBuilder<TodoDbContext>()
            .UseSqliteWasm(new SqliteWasmConnection("Data Source=TestDb.db"))
            .Options;

        return new TodoDbContext(options);
    }

    private static TodoItem CreateItem(Guid id, string title)
    {
        return new TodoItem
        {
            Id = id,
            Title = title,
            Description = "Concurrent transaction serialization",
            UpdatedAt = DateTime.UtcNow
        };
    }
}
