using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.TypeMarshalling;

/// <summary>
/// Regression test: Guid values seeded via HasData must be queryable by
/// Guid parameters. Previously, the provider sent Guid parameters as BLOB
/// but EnsureCreated/HasData stored them as TEXT, causing WHERE Id = @p to
/// return 0 rows despite the data existing.
/// </summary>
internal class GuidHasDataSeedQueryTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Guid_HasDataSeedQuery";

    private static readonly Guid SeededId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var ctx = await Factory.CreateDbContextAsync();

        // FindAsync uses a Guid parameter — this was broken when Guids were bound as BLOB
        var found = await ctx.TodoLists.FindAsync(SeededId);
        if (found is null)
        {
            throw new InvalidOperationException(
                "HasData seeded row not found by Guid PK via FindAsync. " +
                "Provider Guid parameter format does not match HasData INSERT format.");
        }

        if (found.Title != "HasData Guid Seed Test")
        {
            throw new InvalidOperationException(
                $"Seeded row has wrong Name: '{found.Title}'");
        }

        // Also test LINQ Where with Guid comparison
        var queried = await ctx.TodoLists
            .Where(t => t.Id == SeededId)
            .SingleOrDefaultAsync();

        if (queried is null)
        {
            throw new InvalidOperationException(
                "HasData seeded row not found via LINQ Where by Guid.");
        }

        return "OK";
    }
}
