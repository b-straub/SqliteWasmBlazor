using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Relationships;

internal class ForeignKeyEnforcementTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Todo_ForeignKeyEnforcement";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = "PRAGMA foreign_keys";
            var foreignKeysEnabled = Convert.ToInt32(await pragmaCommand.ExecuteScalarAsync());
            if (foreignKeysEnabled != 1)
            {
                throw new InvalidOperationException($"Expected PRAGMA foreign_keys=1, got {foreignKeysEnabled}.");
            }
        }

        context.Todos.Add(new Todo
        {
            Id = Guid.NewGuid(),
            Title = "Invalid orphan todo",
            TodoListId = Guid.NewGuid()
        });

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.Message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) ||
                                          ex.InnerException?.Message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.ChangeTracker.Clear();
        }
        catch (Exception ex) when (ex.Message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
        {
            context.ChangeTracker.Clear();
        }
        finally
        {
            context.ChangeTracker.Clear();
        }

        if (await context.Todos.AnyAsync())
        {
            throw new InvalidOperationException("Invalid orphan todo was persisted despite foreign key enforcement.");
        }

        var listId = Guid.NewGuid();
        var todoId = Guid.NewGuid();
        context.TodoLists.Add(new TodoList
        {
            Id = listId,
            Title = "Foreign key parent",
            CreatedAt = DateTime.UtcNow
        });
        context.Todos.Add(new Todo
        {
            Id = todoId,
            Title = "Foreign key child",
            TodoListId = listId
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM todoLists WHERE Title = {"Foreign key parent"}");

        var childStillExists = await context.Todos.AnyAsync(todo => todo.Id == todoId);
        if (childStillExists)
        {
            throw new InvalidOperationException("Database cascade delete did not remove the child todo.");
        }

        return "OK";
    }
}
