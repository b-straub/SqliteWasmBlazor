using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Transactions;

internal class TransactionBlocksIndependentCommandTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Transaction_BlocksIndependentCommand";

    public override async ValueTask<string?> RunTestAsync()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        await using var firstContext = await Factory.CreateDbContextAsync();
        await using var firstTransaction = await firstContext.Database.BeginTransactionAsync();

        firstContext.TodoItems.Add(CreateItem(firstId, "Held transaction"));
        await firstContext.SaveChangesAsync();

        var independentCommandTask = RunIndependentCommandAsync(secondId);
        var completedBeforeCommit = await Task.WhenAny(independentCommandTask, Task.Delay(250)) == independentCommandTask;
        if (completedBeforeCommit)
        {
            await independentCommandTask;
            throw new InvalidOperationException("Independent command completed while another transaction was active.");
        }

        await firstTransaction.CommitAsync();
        await independentCommandTask;

        await using var verifyContext = await Factory.CreateDbContextAsync();
        var persistedCount = await verifyContext.TodoItems
            .CountAsync(item => item.Id == firstId || item.Id == secondId);
        if (persistedCount != 2)
        {
            throw new InvalidOperationException($"Expected both rows to persist, got {persistedCount}.");
        }

        return "OK";
    }

    private static async Task RunIndependentCommandAsync(Guid itemId)
    {
        await using var context = CreateIndependentContext();

        context.TodoItems.Add(CreateItem(itemId, "Independent command"));
        await context.SaveChangesAsync();
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
            Description = "Transaction command gate",
            UpdatedAt = DateTime.UtcNow
        };
    }
}
