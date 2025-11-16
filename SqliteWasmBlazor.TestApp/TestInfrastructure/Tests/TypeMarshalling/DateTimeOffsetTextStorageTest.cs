using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.TypeMarshalling;

/// <summary>
/// Test that DateTimeOffset values stored as TEXT in SQLite are correctly read back.
/// This tests the fix for GetDateTimeOffset handling string values.
/// </summary>
internal class DateTimeOffsetTextStorageTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "DateTimeOffset_TextStorage";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var testDate = new DateTimeOffset(2024, 11, 16, 10, 30, 0, TimeSpan.Zero);

        // Create entity with DateTimeOffset
        var entity = new TypeTestEntity
        {
            StringValue = "DateTimeOffset TEXT test",
            DateTimeOffsetValue = testDate,
            NullableDateTimeOffsetValue = testDate.AddDays(1)
        };

        context.TypeTests.Add(entity);
        await context.SaveChangesAsync();

        // Read back - this will use GetDateTimeOffset which must handle TEXT columns
        var retrieved = await context.TypeTests.FindAsync(entity.Id);

        if (retrieved is null)
        {
            throw new InvalidOperationException("Entity not found");
        }

        if (retrieved.DateTimeOffsetValue != testDate)
        {
            throw new InvalidOperationException($"DateTimeOffset mismatch. Expected: {testDate}, Got: {retrieved.DateTimeOffsetValue}");
        }

        if (retrieved.NullableDateTimeOffsetValue != testDate.AddDays(1))
        {
            throw new InvalidOperationException($"Nullable DateTimeOffset mismatch. Expected: {testDate.AddDays(1)}, Got: {retrieved.NullableDateTimeOffsetValue}");
        }

        return "OK";
    }
}
