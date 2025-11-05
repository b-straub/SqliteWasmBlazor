using Microsoft.EntityFrameworkCore;
using SQLiteNET.Opfs.TestApp.Data;
using SQLiteNET.Opfs.TestApp.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.TypeMarshalling;

internal class StringValueUnicodeTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "StringValue_Unicode";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var entity = new TypeTestEntity
        {
            StringValue = "Hello 世界 🌍 Привет مرحبا"
        };

        context.TypeTests.Add(entity);
        await context.SaveChangesAsync();

        var retrieved = await context.TypeTests.FindAsync(entity.Id);
        if (retrieved is null)
        {
            throw new InvalidOperationException("Failed to retrieve entity");
        }

        if (retrieved.StringValue != entity.StringValue)
        {
            throw new InvalidOperationException("Unicode string mismatch");
        }

        return "OK";
    }
}
