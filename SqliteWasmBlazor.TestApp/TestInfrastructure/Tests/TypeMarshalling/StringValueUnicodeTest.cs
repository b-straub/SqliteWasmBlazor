using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.TypeMarshalling;

internal class StringValueUnicodeTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "StringValue_Unicode";

    public override async ValueTask<string?> RunTestAsync()
    {
        var entity = new TypeTestEntity
        {
            StringValue = "Hello 世界 🌍 Привет مرحبا"
        };

        await using (var writeCtx = await Factory.CreateDbContextAsync())


        {


            writeCtx.TypeTests.Add(entity);
        await writeCtx.SaveChangesAsync();


        }


        await using var context = await Factory.CreateDbContextAsync();
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
