using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Components.Interop;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.V2Bulk;

/// <summary>
/// V2 bulk round-trip with all nullable fields set to null.
/// Verifies null preservation through MessagePack → SQLite → MessagePack.
/// </summary>
internal class V2BulkNullableAllNullTest(IDbContextFactory<TodoDbContext> factory, ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "V2Bulk_Nullable_AllNull";

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("ISqliteWasmDatabaseService not available");
        }

        // Insert entity with all nullable fields null, non-nullable at defaults
        var original = new TypeTestEntity
        {
            ByteValue = 0,
            ShortValue = 0,
            IntValue = 0,
            LongValue = 0,
            FloatValue = 0,
            DoubleValue = 0,
            DecimalValue = 0,
            BoolValue = false,
            StringValue = "",
            DateTimeValue = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DateTimeOffsetValue = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TimeSpanValue = TimeSpan.Zero,
            GuidValue = Guid.Empty,
            EnumValue = TestEnum.NONE,
            CharValue = '\0',
            IntList = new List<int>(),
            // All nullable fields remain null by default
        };

        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            ctx.TypeTests.Add(original);
            await ctx.SaveChangesAsync();
        }

        // Export
        var columns = MessagePackFileHeaderV2.Create<TypeTestDto>(
            tableName: "TypeTests",
            primaryKeyColumn: "Id",
            recordCount: 0).Columns;

        var exportMetadata = new BulkExportMetadata
        {
            TableName = "TypeTests",
            Columns = columns,
            PrimaryKeyColumn = "Id",
            SchemaHash = SchemaHashGenerator.ComputeHash<TypeTestDto>(),
            DataType = typeof(TypeTestDto).FullName ?? typeof(TypeTestDto).Name,
            Mode = 0,
        };

        var exportedBytes = await DatabaseService.BulkExportAsync("TestDb.db", exportMetadata);

        // Clear
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM TypeTests");
        }

        // Import
        var rowsImported = await DatabaseService.BulkImportAsync("TestDb.db", exportedBytes);
        if (rowsImported != 1)
        {
            throw new InvalidOperationException($"Expected 1 row, got {rowsImported}");
        }

        // Verify all nullable fields are still null
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            var retrieved = await ctx.TypeTests.FirstOrDefaultAsync();
            if (retrieved is null)
            {
                throw new InvalidOperationException("No entity found after import");
            }

            if (retrieved.NullableByteValue is not null) { throw new InvalidOperationException("NullableByteValue should be null"); }
            if (retrieved.NullableShortValue is not null) { throw new InvalidOperationException("NullableShortValue should be null"); }
            if (retrieved.NullableIntValue is not null) { throw new InvalidOperationException("NullableIntValue should be null"); }
            if (retrieved.NullableLongValue is not null) { throw new InvalidOperationException("NullableLongValue should be null"); }
            if (retrieved.NullableFloatValue is not null) { throw new InvalidOperationException("NullableFloatValue should be null"); }
            if (retrieved.NullableDoubleValue is not null) { throw new InvalidOperationException("NullableDoubleValue should be null"); }
            if (retrieved.NullableDecimalValue is not null) { throw new InvalidOperationException("NullableDecimalValue should be null"); }
            if (retrieved.NullableBoolValue is not null) { throw new InvalidOperationException("NullableBoolValue should be null"); }
            if (retrieved.NullableStringValue is not null) { throw new InvalidOperationException("NullableStringValue should be null"); }
            if (retrieved.NullableDateTimeValue is not null) { throw new InvalidOperationException("NullableDateTimeValue should be null"); }
            if (retrieved.NullableDateTimeOffsetValue is not null) { throw new InvalidOperationException("NullableDateTimeOffsetValue should be null"); }
            if (retrieved.NullableTimeSpanValue is not null) { throw new InvalidOperationException("NullableTimeSpanValue should be null"); }
            if (retrieved.NullableGuidValue is not null) { throw new InvalidOperationException("NullableGuidValue should be null"); }
            if (retrieved.NullableEnumValue is not null) { throw new InvalidOperationException("NullableEnumValue should be null"); }
            if (retrieved.NullableCharValue is not null) { throw new InvalidOperationException("NullableCharValue should be null"); }
        }

        return "OK";
    }
}
