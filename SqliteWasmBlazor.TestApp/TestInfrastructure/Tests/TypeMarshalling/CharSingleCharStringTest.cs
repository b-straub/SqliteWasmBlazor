using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.TypeMarshalling;

/// <summary>
/// Test that char values stored as single-character strings are correctly read back.
/// This tests the fix for GetChar handling single-character strings.
/// </summary>
internal class CharSingleCharStringTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Char_SingleCharString";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // Create entity with char values
        var entity = new TypeTestEntity
        {
            StringValue = "Char test",
            CharValue = 'X',
            NullableCharValue = '€' // Unicode character
        };

        context.TypeTests.Add(entity);
        await context.SaveChangesAsync();

        // Read back - this tests GetChar with single-character strings
        var retrieved = await context.TypeTests.FindAsync(entity.Id);

        if (retrieved is null)
        {
            throw new InvalidOperationException("Entity not found");
        }

        if (retrieved.CharValue != 'X')
        {
            throw new InvalidOperationException($"Char mismatch. Expected: 'X', Got: '{retrieved.CharValue}'");
        }

        if (retrieved.NullableCharValue != '€')
        {
            throw new InvalidOperationException($"Nullable Char mismatch. Expected: '€', Got: '{retrieved.NullableCharValue}'");
        }

        return "OK";
    }
}
