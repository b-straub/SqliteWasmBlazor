using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.V2Bulk;

/// <summary>
/// V2 bulk round-trip test: Insert all types via EF Core → BulkExport → clear DB → BulkImport → verify all types match.
/// Tests the complete MessagePack ↔ SQLite type conversion for every supported .NET type.
/// </summary>
internal class V2BulkAllTypesRoundTripTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "V2Bulk_AllTypes_RoundTrip";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Step 1: Insert test entity via EF Core with ALL types populated
        var original = new TypeTestEntity
        {
            ByteValue = 255,
            NullableByteValue = 42,
            ShortValue = -32768,
            NullableShortValue = 1234,
            IntValue = int.MaxValue,
            NullableIntValue = -999,
            LongValue = long.MaxValue,
            NullableLongValue = 123456789L,
            FloatValue = 3.14159f,
            NullableFloatValue = -2.718f,
            DoubleValue = Math.PI,
            NullableDoubleValue = Math.E,
            DecimalValue = 123456.789m,
            NullableDecimalValue = -99999.12345m,
            BoolValue = true,
            NullableBoolValue = false,
            StringValue = "Test String with émojis 🚀",
            NullableStringValue = "nullable string",
            DateTimeValue = new DateTime(2024, 6, 15, 12, 30, 45, DateTimeKind.Utc),
            NullableDateTimeValue = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DateTimeOffsetValue = new DateTimeOffset(2024, 6, 15, 12, 30, 45, TimeSpan.FromHours(2)),
            NullableDateTimeOffsetValue = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5)),
            TimeSpanValue = new TimeSpan(1, 2, 3, 4, 5),
            NullableTimeSpanValue = TimeSpan.FromHours(12.5),
            GuidValue = Guid.NewGuid(),
            NullableGuidValue = Guid.NewGuid(),
            BlobValue = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0xFD },
            EnumValue = TestEnum.SECOND,
            NullableEnumValue = TestEnum.THIRD,
            CharValue = 'A',
            NullableCharValue = '€',
            IntList = new List<int> { 1, 2, 3, 42, 100 }
        };

        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            ctx.TypeTests.Add(original);
            await ctx.SaveChangesAsync();
        }

        // Step 2: BulkExport via worker
        var schemaHash = SchemaHashGenerator.ComputeHash<TypeTestDto>();
        var columns = MessagePackFileHeaderV2.Create<TypeTestDto>(
            tableName: "TypeTests",
            primaryKeyColumn: "Id",
            recordCount: 0).Columns;

        var exportMetadata = new BulkExportMetadata
        {
            TableName = "TypeTests",
            Columns = columns,
            PrimaryKeyColumn = "Id",
            SchemaHash = schemaHash,
            DataType = typeof(TypeTestDto).FullName ?? typeof(TypeTestDto).Name,
            Mode = 0,
            Where = (string?)null,
            OrderBy = "\"Id\" ASC"
        };

        var exportedBytes = await DatabaseService.BulkExportAsync("TestDb.db", exportMetadata);

        // Step 3: Clear the table
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM TypeTests");
            var count = await ctx.TypeTests.CountAsync();
            if (count != 0)
            {
                throw new InvalidOperationException($"Table not cleared: {count} rows remain");
            }
        }

        // Step 4: BulkImport via worker
        var rowsImported = await DatabaseService.BulkImportAsync("TestDb.db", exportedBytes);
        if (rowsImported != 1)
        {
            throw new InvalidOperationException($"Expected 1 row imported, got {rowsImported}");
        }

        // Step 5: Read back via EF Core and verify ALL fields
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var retrieved = await ctx.TypeTests.FirstOrDefaultAsync();
            if (retrieved is null)
            {
                throw new InvalidOperationException("No entity found after import");
            }

            AssertEqual("Id", original.Id, retrieved.Id);
            AssertEqual("ByteValue", original.ByteValue, retrieved.ByteValue);
            AssertEqual("NullableByteValue", original.NullableByteValue, retrieved.NullableByteValue);
            AssertEqual("ShortValue", original.ShortValue, retrieved.ShortValue);
            AssertEqual("NullableShortValue", original.NullableShortValue, retrieved.NullableShortValue);
            AssertEqual("IntValue", original.IntValue, retrieved.IntValue);
            AssertEqual("NullableIntValue", original.NullableIntValue, retrieved.NullableIntValue);
            AssertEqual("LongValue", original.LongValue, retrieved.LongValue);
            AssertEqual("NullableLongValue", original.NullableLongValue, retrieved.NullableLongValue);

            AssertFloatEqual("FloatValue", original.FloatValue, retrieved.FloatValue, 0.0001f);
            AssertFloatEqual("NullableFloatValue", original.NullableFloatValue, retrieved.NullableFloatValue, 0.0001f);

            AssertEqual("DoubleValue", original.DoubleValue, retrieved.DoubleValue);
            AssertEqual("NullableDoubleValue", original.NullableDoubleValue, retrieved.NullableDoubleValue);

            AssertEqual("DecimalValue", original.DecimalValue, retrieved.DecimalValue);
            AssertEqual("NullableDecimalValue", original.NullableDecimalValue, retrieved.NullableDecimalValue);

            AssertEqual("BoolValue", original.BoolValue, retrieved.BoolValue);
            AssertEqual("NullableBoolValue", original.NullableBoolValue, retrieved.NullableBoolValue);

            AssertEqual("StringValue", original.StringValue, retrieved.StringValue);
            AssertEqual("NullableStringValue", original.NullableStringValue, retrieved.NullableStringValue);

            // DateTime: compare to second precision (SQLite TEXT may lose sub-second precision depending on format)
            AssertDateTimeEqual("DateTimeValue", original.DateTimeValue, retrieved.DateTimeValue);
            AssertDateTimeEqual("NullableDateTimeValue", original.NullableDateTimeValue, retrieved.NullableDateTimeValue);

            // DateTimeOffset: compare UTC time (offset may be normalized)
            AssertDateTimeOffsetEqual("DateTimeOffsetValue", original.DateTimeOffsetValue, retrieved.DateTimeOffsetValue);
            AssertDateTimeOffsetEqual("NullableDateTimeOffsetValue", original.NullableDateTimeOffsetValue, retrieved.NullableDateTimeOffsetValue);

            // TimeSpan: compare to millisecond precision
            AssertTimeSpanEqual("TimeSpanValue", original.TimeSpanValue, retrieved.TimeSpanValue);
            AssertTimeSpanEqual("NullableTimeSpanValue", original.NullableTimeSpanValue, retrieved.NullableTimeSpanValue);

            AssertEqual("GuidValue", original.GuidValue, retrieved.GuidValue);
            AssertEqual("NullableGuidValue", original.NullableGuidValue, retrieved.NullableGuidValue);

            if (original.BlobValue is null || retrieved.BlobValue is null || !original.BlobValue.SequenceEqual(retrieved.BlobValue))
            {
                throw new InvalidOperationException("BlobValue mismatch");
            }

            AssertEqual("EnumValue", original.EnumValue, retrieved.EnumValue);
            AssertEqual("NullableEnumValue", original.NullableEnumValue, retrieved.NullableEnumValue);

            AssertEqual("CharValue", original.CharValue, retrieved.CharValue);
            AssertEqual("NullableCharValue", original.NullableCharValue, retrieved.NullableCharValue);

            if (!original.IntList.SequenceEqual(retrieved.IntList))
            {
                throw new InvalidOperationException($"IntList mismatch: [{string.Join(",", original.IntList)}] vs [{string.Join(",", retrieved.IntList)}]");
            }
        }

        return "OK";
    }

    private static void AssertEqual<T>(string name, T expected, T actual)
    {
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException($"{name} mismatch: expected '{expected}', got '{actual}'");
        }
    }

    private static void AssertFloatEqual(string name, float? expected, float? actual, float tolerance)
    {
        if (expected is null && actual is null)
        {
            return;
        }

        if (expected is null || actual is null || Math.Abs(expected.Value - actual.Value) > tolerance)
        {
            throw new InvalidOperationException($"{name} mismatch: expected '{expected}', got '{actual}'");
        }
    }

    private static void AssertDateTimeEqual(string name, DateTime? expected, DateTime? actual)
    {
        if (expected is null && actual is null)
        {
            return;
        }

        if (expected is null || actual is null || Math.Abs((expected.Value - actual.Value).TotalSeconds) > 1)
        {
            throw new InvalidOperationException($"{name} mismatch: expected '{expected:O}', got '{actual:O}'");
        }
    }

    private static void AssertDateTimeOffsetEqual(string name, DateTimeOffset? expected, DateTimeOffset? actual)
    {
        if (expected is null && actual is null)
        {
            return;
        }

        if (expected is null || actual is null || Math.Abs((expected.Value.UtcDateTime - actual.Value.UtcDateTime).TotalSeconds) > 1)
        {
            throw new InvalidOperationException($"{name} mismatch: expected '{expected:O}', got '{actual:O}'");
        }
    }

    private static void AssertTimeSpanEqual(string name, TimeSpan? expected, TimeSpan? actual)
    {
        if (expected is null && actual is null)
        {
            return;
        }

        if (expected is null || actual is null || Math.Abs((expected.Value - actual.Value).TotalMilliseconds) > 1)
        {
            throw new InvalidOperationException($"{name} mismatch: expected '{expected}', got '{actual}'");
        }
    }
}
