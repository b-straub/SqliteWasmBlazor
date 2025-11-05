using Microsoft.EntityFrameworkCore;
using SQLiteNET.Opfs.TestApp.Data;
using SQLiteNET.Opfs.TestApp.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.TypeMarshalling;

internal class NullableTypesAllNullTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "NullableTypes_AllNull";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var entity = new TypeTestEntity
        {
            NullableByteValue = null,
            NullableIntValue = null,
            NullableStringValue = null,
            NullableGuidValue = null
        };

        context.TypeTests.Add(entity);
        await context.SaveChangesAsync();

        var retrieved = await context.TypeTests.FindAsync(entity.Id);
        if (retrieved is null)
        {
            throw new InvalidOperationException("Failed to retrieve entity");
        }

        if (retrieved.NullableByteValue is not null)
        {
            throw new InvalidOperationException("NullableByteValue should be null");
        }

        if (retrieved.NullableIntValue is not null)
        {
            throw new InvalidOperationException("NullableIntValue should be null");
        }

        if (retrieved.NullableStringValue is not null)
        {
            throw new InvalidOperationException("NullableStringValue should be null");
        }

        if (retrieved.NullableGuidValue is not null)
        {
            throw new InvalidOperationException("NullableGuidValue should be null");
        }

        return "OK";
    }
}
