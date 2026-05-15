using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.TypeMarshalling;

/// <summary>
/// Test that Guid values stored as UTF-8 byte arrays (non-16-byte) are correctly read back.
/// This tests the fix for GetGuid handling UTF-8 encoded Guid strings.
/// </summary>
internal class GuidUtf8ByteArrayTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "Guid_Utf8ByteArray";

    public override async ValueTask<string?> RunTestAsync()
    {
        var testGuid = Guid.NewGuid();

        // Create entity with Guid
        var entity = new TypeTestEntity
        {
            StringValue = "Guid test",
            GuidValue = testGuid,
            NullableGuidValue = Guid.NewGuid()
        };

        await using (var writeCtx = await Factory.CreateDbContextAsync())


        {


            writeCtx.TypeTests.Add(entity);
        await writeCtx.SaveChangesAsync();


        }


        // Read back - this tests GetGuid
        await using var context = await Factory.CreateDbContextAsync();
        var retrieved = await context.TypeTests.FindAsync(entity.Id);

        if (retrieved is null)
        {
            throw new InvalidOperationException("Entity not found");
        }

        if (retrieved.GuidValue != testGuid)
        {
            throw new InvalidOperationException($"Guid mismatch. Expected: {testGuid}, Got: {retrieved.GuidValue}");
        }

        if (retrieved.NullableGuidValue != entity.NullableGuidValue)
        {
            throw new InvalidOperationException($"Nullable Guid mismatch. Expected: {entity.NullableGuidValue}, Got: {retrieved.NullableGuidValue}");
        }

        return "OK";
    }
}
