using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.TypeMarshalling;

/// <summary>
/// Regression test: Guid values written via EF Core must be queryable by
/// Guid parameters. Previously, the provider sent Guid parameters as BLOB
/// but stored them as TEXT, causing WHERE Id = @p to return 0 rows.
///
/// Tests both write-then-FindAsync and write-then-LINQ-Where paths.
/// Uses CryptoSync's HasData seed pattern (Guid PK in system tables) as
/// the real-world scenario this regression protects.
/// </summary>
internal class GuidHasDataSeedQueryTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Guid_HasDataSeedQuery";

    private static readonly Guid TestId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    public override async ValueTask<string?> RunTestAsync()
    {
        // Write a TodoList with a known Guid PK
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            ctx.TodoLists.Add(new TodoList
            {
                Id = TestId,
                Title = "Guid Query Regression Test",
                CreatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        // Query back by Guid PK — this is the path that was broken
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var found = await ctx.TodoLists.FindAsync(TestId);
            if (found is null)
            {
                throw new InvalidOperationException(
                    "Row not found by Guid PK via FindAsync. " +
                    "Provider Guid parameter format does not match stored format.");
            }

            if (found.Title != "Guid Query Regression Test")
            {
                throw new InvalidOperationException(
                    $"Wrong Title: '{found.Title}'");
            }

            // Also test LINQ Where with Guid comparison
            var queried = await ctx.TodoLists
                .Where(t => t.Id == TestId)
                .SingleOrDefaultAsync();

            if (queried is null)
            {
                throw new InvalidOperationException(
                    "Row not found via LINQ Where by Guid.");
            }
        }

        return "OK";
    }
}
