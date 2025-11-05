namespace SqliteWasm.Demo.Models;

/// <summary>
/// Entity for testing all .NET type mappings and JSON marshalling between C# and JavaScript worker.
/// </summary>
public class TypeTestEntity
{
    public int Id { get; set; }

    // Integer types
    public byte ByteValue { get; set; }
    public byte? NullableByteValue { get; set; }
    public short ShortValue { get; set; }
    public short? NullableShortValue { get; set; }
    public int IntValue { get; set; }
    public int? NullableIntValue { get; set; }
    public long LongValue { get; set; }
    public long? NullableLongValue { get; set; }

    // Floating point types
    public float FloatValue { get; set; }
    public float? NullableFloatValue { get; set; }
    public double DoubleValue { get; set; }
    public double? NullableDoubleValue { get; set; }
    public decimal DecimalValue { get; set; }
    public decimal? NullableDecimalValue { get; set; }

    // Boolean
    public bool BoolValue { get; set; }
    public bool? NullableBoolValue { get; set; }

    // String
    public string StringValue { get; set; } = string.Empty;
    public string? NullableStringValue { get; set; }

    // DateTime types
    public DateTime DateTimeValue { get; set; }
    public DateTime? NullableDateTimeValue { get; set; }
    public DateTimeOffset DateTimeOffsetValue { get; set; }
    public DateTimeOffset? NullableDateTimeOffsetValue { get; set; }
    public TimeSpan TimeSpanValue { get; set; }
    public TimeSpan? NullableTimeSpanValue { get; set; }

    // Guid
    public Guid GuidValue { get; set; }
    public Guid? NullableGuidValue { get; set; }

    // Binary data
    public byte[]? BlobValue { get; set; }

    // Enum
    public TestEnum EnumValue { get; set; }
    public TestEnum? NullableEnumValue { get; set; }

    // Char (stored as string in SQLite)
    public char CharValue { get; set; }
    public char? NullableCharValue { get; set; }

    // Collection (stored as JSON TEXT)
    public List<int> IntList { get; set; } = new();
}

public enum TestEnum
{
    None = 0,
    First = 1,
    Second = 2,
    Third = 3
}
