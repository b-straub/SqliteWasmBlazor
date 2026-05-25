using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class NativeAggregateWindowFunctionsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_NativeAggregateWindowFunctions";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await VerifyAggregateFunctionsAsync(connection);
        await VerifyWindowFunctionsAsync(connection);

        return "OK";
    }

    private static async Task VerifyAggregateFunctionsAsync(System.Data.Common.DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH data(category, name, amount) AS (
                VALUES
                    ('food', 'alpha', 10),
                    ('food', 'beta', 20),
                    ('food', 'gamma', 20),
                    ('drink', 'delta', 30)
            )
            SELECT
                count(*) AS CountValue,
                count(amount) AS CountAmountValue,
                count(*) FILTER (WHERE category = 'food') AS FilteredCountValue,
                sum(amount) AS SumValue,
                avg(amount) AS AverageValue,
                total(amount) AS TotalValue,
                min(amount) AS MinValue,
                max(amount) AS MaxValue,
                group_concat(name, '|') AS GroupConcatValue,
                string_agg(name, '|') AS StringAggValue,
                json_group_array(name) AS JsonGroupArrayValue,
                json_group_object(name, amount) AS JsonGroupObjectValue
            FROM data
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one native aggregate function row.");
        }

        AssertEqual(4, reader.GetInt32(reader.GetOrdinal("CountValue")), "count");
        AssertEqual(4, reader.GetInt32(reader.GetOrdinal("CountAmountValue")), "count(column)");
        AssertEqual(3, reader.GetInt32(reader.GetOrdinal("FilteredCountValue")), "count filter");
        AssertEqual(80, reader.GetInt32(reader.GetOrdinal("SumValue")), "sum");
        AssertClose(20.0, reader.GetDouble(reader.GetOrdinal("AverageValue")), "avg");
        AssertClose(80.0, reader.GetDouble(reader.GetOrdinal("TotalValue")), "total");
        AssertEqual(10, reader.GetInt32(reader.GetOrdinal("MinValue")), "min");
        AssertEqual(30, reader.GetInt32(reader.GetOrdinal("MaxValue")), "max");
        AssertDelimitedNames(reader.GetString(reader.GetOrdinal("GroupConcatValue")), "group_concat");
        AssertDelimitedNames(reader.GetString(reader.GetOrdinal("StringAggValue")), "string_agg");
        AssertJsonArray(reader.GetString(reader.GetOrdinal("JsonGroupArrayValue")), "json_group_array");
        AssertJsonObject(reader.GetString(reader.GetOrdinal("JsonGroupObjectValue")), "json_group_object");

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected only one native aggregate function row.");
        }
    }

    private static async Task VerifyWindowFunctionsAsync(System.Data.Common.DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH data(category, name, amount) AS (
                VALUES
                    ('food', 'alpha', 10),
                    ('food', 'beta', 20),
                    ('food', 'gamma', 20),
                    ('drink', 'delta', 30)
            )
            SELECT
                name AS NameValue,
                amount AS AmountValue,
                row_number() OVER (ORDER BY amount, name) AS RowNumberValue,
                rank() OVER (ORDER BY amount) AS RankValue,
                dense_rank() OVER (ORDER BY amount) AS DenseRankValue,
                percent_rank() OVER (ORDER BY amount) AS PercentRankValue,
                cume_dist() OVER (ORDER BY amount) AS CumeDistValue,
                ntile(2) OVER (ORDER BY amount, name) AS NtileValue,
                lag(amount, 1, -1) OVER (ORDER BY amount, name) AS LagValue,
                lead(amount, 1, -1) OVER (ORDER BY amount, name) AS LeadValue,
                first_value(name) OVER (
                    ORDER BY amount, name
                    ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
                ) AS FirstNameValue,
                last_value(name) OVER (
                    ORDER BY amount, name
                    ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
                ) AS LastNameValue
            FROM data
            ORDER BY amount, name
            """;

        var expected = new[]
        {
            new WindowRow("alpha", 10, 1, 1, 1, 0.0, 0.25, 1, -1, 20),
            new WindowRow("beta", 20, 2, 2, 2, 1.0 / 3.0, 0.75, 1, 10, 20),
            new WindowRow("gamma", 20, 3, 2, 2, 1.0 / 3.0, 0.75, 2, 20, 30),
            new WindowRow("delta", 30, 4, 4, 3, 1.0, 1.0, 2, 20, -1),
        };

        await using var reader = await command.ExecuteReaderAsync();
        foreach (var row in expected)
        {
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException(
                    $"Expected native window function row for {row.Name}.");
            }

            AssertEqual(row.Name, reader.GetString(reader.GetOrdinal("NameValue")), "window name");
            AssertEqual(row.Amount, reader.GetInt32(reader.GetOrdinal("AmountValue")), "window amount");
            AssertEqual(row.RowNumber, reader.GetInt32(reader.GetOrdinal("RowNumberValue")), "row_number");
            AssertEqual(row.Rank, reader.GetInt32(reader.GetOrdinal("RankValue")), "rank");
            AssertEqual(row.DenseRank, reader.GetInt32(reader.GetOrdinal("DenseRankValue")), "dense_rank");
            AssertClose(row.PercentRank, reader.GetDouble(reader.GetOrdinal("PercentRankValue")), "percent_rank");
            AssertClose(row.CumeDist, reader.GetDouble(reader.GetOrdinal("CumeDistValue")), "cume_dist");
            AssertEqual(row.Ntile, reader.GetInt32(reader.GetOrdinal("NtileValue")), "ntile");
            AssertEqual(row.Lag, reader.GetInt32(reader.GetOrdinal("LagValue")), "lag");
            AssertEqual(row.Lead, reader.GetInt32(reader.GetOrdinal("LeadValue")), "lead");
            AssertEqual("alpha", reader.GetString(reader.GetOrdinal("FirstNameValue")), "first_value");
            AssertEqual("delta", reader.GetString(reader.GetOrdinal("LastNameValue")), "last_value");
        }

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected only four native window function rows.");
        }
    }

    private static void AssertDelimitedNames(string value, string functionName)
    {
        var names = value.Split('|').OrderBy(name => name).ToArray();
        AssertSequenceEqual(["alpha", "beta", "delta", "gamma"], names, functionName);
    }

    private static void AssertJsonArray(string value, string functionName)
    {
        using var document = System.Text.Json.JsonDocument.Parse(value);
        var names = document.RootElement.EnumerateArray()
            .Select(element => element.GetString())
            .OrderBy(name => name)
            .ToArray();

        AssertSequenceEqual(["alpha", "beta", "delta", "gamma"], names, functionName);
    }

    private static void AssertJsonObject(string value, string functionName)
    {
        using var document = System.Text.Json.JsonDocument.Parse(value);
        var values = document.RootElement.EnumerateObject()
            .OrderBy(property => property.Name)
            .Select(property => $"{property.Name}:{property.Value.GetInt32()}")
            .ToArray();

        AssertSequenceEqual(["alpha:10", "beta:20", "delta:30", "gamma:20"], values, functionName);
    }

    private static void AssertSequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string functionName)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"SQLite function {functionName} returned [{string.Join(", ", actual)}]; " +
                $"expected [{string.Join(", ", expected)}].");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string functionName)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"SQLite function {functionName} returned {actual}; expected {expected}.");
        }
    }

    private static void AssertClose(double expected, double actual, string functionName)
    {
        if (Math.Abs(expected - actual) > 0.0000001)
        {
            throw new InvalidOperationException(
                $"SQLite function {functionName} returned {actual}; expected {expected}.");
        }
    }

    private sealed record WindowRow(
        string Name,
        int Amount,
        int RowNumber,
        int Rank,
        int DenseRank,
        double PercentRank,
        double CumeDist,
        int Ntile,
        int Lag,
        int Lead);
}
