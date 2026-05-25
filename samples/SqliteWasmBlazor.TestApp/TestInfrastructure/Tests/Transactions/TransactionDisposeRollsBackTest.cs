using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Transactions;

internal class TransactionDisposeRollsBackTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Transaction_DisposeRollsBack";

    public override async ValueTask<string?> RunTestAsync()
    {
        var asyncDisposedId = Guid.NewGuid();
        var syncDisposedId = Guid.NewGuid();

        await using (var context = await Factory.CreateDbContextAsync())
        {
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                context.TodoItems.Add(CreateItem(asyncDisposedId, "Async dispose rollback"));
                await context.SaveChangesAsync();
            }
        }

        await AssertRolledBackAsync(asyncDisposedId, "async-disposed transaction");
        await AssertCanBeginNewTransactionAsync("after async dispose rollback");

        await using (var context = await Factory.CreateDbContextAsync())
        {
            var transaction = await context.Database.BeginTransactionAsync();
            context.TodoItems.Add(CreateItem(syncDisposedId, "Sync dispose rollback"));
            await context.SaveChangesAsync();
            transaction.Dispose();
        }

        await AssertRolledBackAsync(syncDisposedId, "sync-disposed transaction");
        await AssertCanBeginNewTransactionAsync("after sync dispose rollback");

        return "OK";
    }

    private static TodoItem CreateItem(Guid id, string title)
    {
        return new TodoItem
        {
            Id = id,
            Title = title,
            Description = "Dispose without explicit commit should roll back",
            UpdatedAt = DateTime.UtcNow
        };
    }

    private async Task AssertRolledBackAsync(Guid id, string operation)
    {
        await using var verifyContext = await Factory.CreateDbContextAsync();
        var exists = await verifyContext.TodoItems.AnyAsync(item => item.Id == id);
        if (exists)
        {
            throw new InvalidOperationException($"Expected {operation} to roll back inserted row {id}.");
        }
    }

    private async Task AssertCanBeginNewTransactionAsync(string operation)
    {
        await using var probeContext = await Factory.CreateDbContextAsync();
        await using var probeTransaction = await probeContext.Database.BeginTransactionAsync();
        await probeTransaction.RollbackAsync();
    }
}
