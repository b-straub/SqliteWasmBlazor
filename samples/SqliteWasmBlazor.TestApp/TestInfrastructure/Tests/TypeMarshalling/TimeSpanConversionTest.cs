using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.TypeMarshalling;

/// <summary>
/// Test that TimeSpan values are correctly stored and retrieved, including:
/// - Direct TimeSpan values
/// - TEXT storage (string parsing)
/// - FLOAT/INTEGER storage (stored as days, not milliseconds)
/// </summary>
internal class TimeSpanConversionTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "TimeSpan_Conversion";

    public override async ValueTask<string?> RunTestAsync()
    {
        var testTimeSpan = new TimeSpan(2, 14, 30, 45); // 2 days, 14 hours, 30 minutes, 45 seconds

        // Create entity with TimeSpan
        var entity = new TypeTestEntity
        {
            StringValue = "TimeSpan test",
            TimeSpanValue = testTimeSpan,
            NullableTimeSpanValue = TimeSpan.FromHours(5.5)
        };

        await using (var writeCtx = await Factory.CreateDbContextAsync())


        {


            writeCtx.TypeTests.Add(entity);
        await writeCtx.SaveChangesAsync();


        }


        // Read back - this tests GetTimeSpan
        await using var context = await Factory.CreateDbContextAsync();
        var retrieved = await context.TypeTests.FindAsync(entity.Id);

        if (retrieved is null)
        {
            throw new InvalidOperationException("Entity not found");
        }

        if (retrieved.TimeSpanValue != testTimeSpan)
        {
            throw new InvalidOperationException($"TimeSpan mismatch. Expected: {testTimeSpan}, Got: {retrieved.TimeSpanValue}");
        }

        if (retrieved.NullableTimeSpanValue != TimeSpan.FromHours(5.5))
        {
            throw new InvalidOperationException($"Nullable TimeSpan mismatch. Expected: {TimeSpan.FromHours(5.5)}, Got: {retrieved.NullableTimeSpanValue}");
        }

        return "OK";
    }
}
