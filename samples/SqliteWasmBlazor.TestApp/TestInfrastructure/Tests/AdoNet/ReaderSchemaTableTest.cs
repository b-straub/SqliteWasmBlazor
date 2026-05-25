using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class ReaderSchemaTableTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_ReaderSchemaTable";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await ExecuteAsync(connection, """
            CREATE TABLE SchemaTableItems (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                Quantity INTEGER NOT NULL,
                Price REAL NOT NULL,
                Payload BLOB NULL
            )
            """);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Quantity, Price, Payload
            FROM SchemaTableItems
            WHERE Id = -1
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (reader.GetFieldType(0) != typeof(long) ||
            reader.GetFieldType(1) != typeof(string) ||
            reader.GetFieldType(2) != typeof(long) ||
            reader.GetFieldType(3) != typeof(double) ||
            reader.GetFieldType(4) != typeof(byte[]))
        {
            throw new InvalidOperationException("GetFieldType did not expose native SQLite metadata before Read().");
        }

        var schemaTable = reader.GetSchemaTable();
        if (schemaTable is null)
        {
            throw new InvalidOperationException("GetSchemaTable returned null.");
        }

        if (schemaTable.Rows.Count != 5)
        {
            throw new InvalidOperationException($"Expected 5 schema rows, got {schemaTable.Rows.Count}.");
        }

        AssertSchemaRow(schemaTable.Rows[0], "Id", 0, typeof(long), DbType.Int64, false);
        AssertSchemaRow(schemaTable.Rows[1], "Name", 1, typeof(string), DbType.String, false);
        AssertSchemaRow(schemaTable.Rows[2], "Quantity", 2, typeof(long), DbType.Int64, false);
        AssertSchemaRow(schemaTable.Rows[3], "Price", 3, typeof(double), DbType.Double, false);
        AssertSchemaRow(schemaTable.Rows[4], "Payload", 4, typeof(byte[]), DbType.Binary, true);

        var columnSchema = reader.GetColumnSchema();
        if (columnSchema.Count != 5)
        {
            throw new InvalidOperationException($"Expected 5 column schema entries, got {columnSchema.Count}.");
        }

        AssertColumnSchema(columnSchema[0], "Id", 0, "INTEGER", typeof(long), false);
        AssertColumnSchema(columnSchema[1], "Name", 1, "TEXT", typeof(string), false);
        AssertColumnSchema(columnSchema[2], "Quantity", 2, "INTEGER", typeof(long), false);
        AssertColumnSchema(columnSchema[3], "Price", 3, "REAL", typeof(double), false);
        AssertColumnSchema(columnSchema[4], "Payload", 4, "BLOB", typeof(byte[]), true);

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Schema table test query unexpectedly returned a row.");
        }

        return "OK";
    }

    private static async Task ExecuteAsync(
        System.Data.Common.DbConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void AssertSchemaRow(
        DataRow row,
        string expectedName,
        int expectedOrdinal,
        Type expectedType,
        DbType expectedProviderType,
        bool expectedIsLong)
    {
        if ((string)row[SchemaTableColumn.ColumnName] != expectedName ||
            (int)row[SchemaTableColumn.ColumnOrdinal] != expectedOrdinal ||
            row[SchemaTableColumn.DataType] as Type != expectedType ||
            (int)row[SchemaTableColumn.ProviderType] != (int)expectedProviderType ||
            (bool)row[SchemaTableColumn.IsLong] != expectedIsLong ||
            (string)row[SchemaTableColumn.BaseColumnName] != expectedName)
        {
            throw new InvalidOperationException(
                $"Unexpected schema row for {expectedName}: " +
                $"{row[SchemaTableColumn.ColumnName]} / " +
                $"{row[SchemaTableColumn.ColumnOrdinal]} / " +
                $"{row[SchemaTableColumn.DataType]} / " +
                $"{row[SchemaTableColumn.ProviderType]}.");
        }
    }

    private static void AssertColumnSchema(
        DbColumn column,
        string expectedName,
        int expectedOrdinal,
        string expectedDataTypeName,
        Type expectedType,
        bool expectedIsLong)
    {
        if (column.ColumnName != expectedName ||
            column.ColumnOrdinal != expectedOrdinal ||
            column.BaseColumnName != expectedName ||
            !string.Equals(column.DataTypeName, expectedDataTypeName, StringComparison.OrdinalIgnoreCase) ||
            column.DataType != expectedType ||
            column.IsLong != expectedIsLong ||
            column.AllowDBNull != true)
        {
            throw new InvalidOperationException(
                $"Unexpected column schema for {expectedName}: " +
                $"{column.ColumnName} / {column.ColumnOrdinal} / " +
                $"{column.DataTypeName} / {column.DataType} / {column.IsLong}.");
        }
    }
}
