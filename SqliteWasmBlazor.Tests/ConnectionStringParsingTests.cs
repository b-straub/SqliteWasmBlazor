using SqliteWasmBlazor;

namespace SqliteWasmBlazor.Tests;

public class ConnectionStringParsingTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Data Source=test.db", null)]
    [InlineData("Data Source=test.db;Password=abc", "abc")]
    [InlineData("Data Source=test.db;Password=has=equals", "has=equals")] // '=' in value
    // Case-insensitive key
    [InlineData("Data Source=test.db;password=lower", "lower")]
    [InlineData("Data Source=test.db;PASSWORD=upper", "upper")]
    // Whitespace around key / value
    [InlineData("Data Source=test.db; Password = abc ", "abc")]
    // Empty value after trim → null
    [InlineData("Data Source=test.db;Password=", null)]
    [InlineData("Data Source=test.db;Password=   ", null)]
    // Password appears without Data Source
    [InlineData("Password=standalone", "standalone")]
    // Password appears before Data Source
    [InlineData("Password=first;Data Source=test.db", "first")]
    public void ParsePassword_ReturnsExpected(string? connectionString, string? expected)
    {
        Assert.Equal(expected, SqliteWasmConnection.ParsePassword(connectionString));
    }
}
