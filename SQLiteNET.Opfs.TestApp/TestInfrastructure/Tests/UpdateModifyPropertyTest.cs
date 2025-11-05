using Microsoft.EntityFrameworkCore;
using SQLiteNET.Opfs.TestApp.Data;
using SQLiteNET.Opfs.TestApp.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests;

internal class UpdateModifyPropertyTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "UpdateModifyProperty";

    public override async ValueTask<string?> RunTestAsync()
    {
        int itemId;

        // Create entity
        await using (var context = await Factory.CreateDbContextAsync())
        {
            var item = new TodoItem
            {
                Title = "Original Title",
                Description = "Original Description",
                CreatedAt = DateTime.UtcNow
            };

            context.TodoItems.Add(item);
            await context.SaveChangesAsync();
            itemId = item.Id;
            Console.WriteLine($"[UpdateModifyProperty] Created item with Id={itemId}");
        }

        // Update in separate context
        await using (var updateContext = await Factory.CreateDbContextAsync())
        {
            var itemToUpdate = await updateContext.TodoItems.FindAsync(itemId);
            if (itemToUpdate is null)
            {
                throw new InvalidOperationException("Failed to find item for update");
            }

            Console.WriteLine($"[UpdateModifyProperty] Found item: Id={itemToUpdate.Id}, Title={itemToUpdate.Title}");
            Console.WriteLine($"[UpdateModifyProperty] Entity state before modification: {updateContext.Entry(itemToUpdate).State}");

            itemToUpdate.Title = "Updated Title";
            itemToUpdate.Description = "Updated Description";

            Console.WriteLine($"[UpdateModifyProperty] Entity state after modification: {updateContext.Entry(itemToUpdate).State}");
            Console.WriteLine($"[UpdateModifyProperty] About to SaveChangesAsync...");

            await updateContext.SaveChangesAsync();

            Console.WriteLine($"[UpdateModifyProperty] SaveChangesAsync completed");
        }

        // Verify with fresh context
        await using (var verifyContext = await Factory.CreateDbContextAsync())
        {
            var updated = await verifyContext.TodoItems.FindAsync(itemId);
            if (updated is null)
            {
                throw new InvalidOperationException("Failed to find updated entity");
            }

            if (updated.Title != "Updated Title")
            {
                throw new InvalidOperationException("Title not updated");
            }

            if (updated.Description != "Updated Description")
            {
                throw new InvalidOperationException("Description not updated");
            }
        }

        return "OK";
    }
}
