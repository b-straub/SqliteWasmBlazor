using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.AdoNet;

internal class NativeScalarFunctionsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "AdoNet_NativeScalarFunctions";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                lower('MiXeD') AS LowerValue,
                upper('MiXeD') AS UpperValue,
                substr('abcdef', 2, 3) AS SubstringValue,
                instr('abcdef', 'de') AS InStringValue,
                length('abcdef') AS LengthValue,
                trim('  padded  ') AS TrimValue,
                ltrim('  left') AS LeftTrimValue,
                rtrim('right  ') AS RightTrimValue,
                replace('abcabc', 'ab', 'xy') AS ReplaceValue,
                char(65, 66, 67) AS CharValue,
                unicode('A') AS UnicodeValue,
                coalesce(NULL, 'fallback') AS CoalesceValue,
                ifnull(NULL, 'fallback') AS IfNullValue,
                nullif('same', 'same') AS NullIfValue,
                iif(1, 'yes', 'no') AS IifValue,
                if(0, 'yes', 'no') AS IfValue,
                concat('a', NULL, 'b') AS ConcatValue,
                concat_ws('|', 'a', NULL, 'b') AS ConcatWsValue,
                printf('%04d', 7) AS PrintfValue,
                format('%s-%d', 'item', 7) AS FormatValue,
                quote('quoted') AS QuoteValue,
                length(zeroblob(4)) AS ZeroBlobLengthValue,
                octet_length('é') AS TextOctetLengthValue,
                octet_length(x'000102') AS BlobOctetLengthValue,
                length(randomblob(8)) AS RandomBlobLengthValue,
                likely(1) AS LikelyValue,
                unlikely(0) AS UnlikelyValue,
                likelihood(1, 0.25) AS LikelihoodValue,
                round(12.345, 2) AS RoundValue,
                abs(-42) AS AbsValue,
                sin(0.5) AS SinValue,
                acos(0.5) AS AcosValue,
                atan2(0.5, 2.0) AS Atan2Value,
                ln(0.5) AS LnValue,
                log(2, 8) AS LogBaseValue,
                log2(8) AS Log2Value,
                log10(100) AS Log10Value,
                sign(-42) AS SignValue,
                trunc(3.9) AS TruncValue,
                radians(180) AS RadiansValue,
                degrees(3.141592653589793) AS DegreesValue,
                typeof(42) AS TypeOfValue,
                sqlite_version() AS SqliteVersionValue,
                length(sqlite_source_id()) > 0 AS SqliteSourceIdPresentValue,
                sqlite_compileoption_used('ENABLE_FTS5') AS Fts5CompileOptionValue,
                sqlite_compileoption_get(0) IS NOT NULL AS CompileOptionGetValue,
                hex(x'0AFF') AS HexValue,
                hex(unhex('0A-FF', '-')) AS UnhexValue,
                soundex('Euler') AS SoundexEulerValue,
                soundex('Pfister') AS SoundexPfisterValue,
                soundex('123') AS SoundexNoLettersValue,
                typeof(sha3('abc')) AS Sha3TypeValue,
                hex(sha3('abc')) AS Sha3DefaultValue,
                hex(sha3(x'616263', 256)) AS Sha3BlobValue,
                length(sha3('abc', 224)) AS Sha3_224LengthValue,
                length(sha3('abc', 384)) AS Sha3_384LengthValue,
                length(sha3('abc', 512)) AS Sha3_512LengthValue,
                unistr('\u0041\u0042') AS UnistrValue,
                unistr_quote('A' || char(10) || 'B') AS UnistrQuoteValue,
                strftime('%Y-%m-%d', '2026-05-24 09:41:00') AS DateValue,
                json_extract('{"answer":42}', '$.answer') AS JsonValue
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected one native function row.");
        }

        AssertEqual("mixed", reader.GetString(reader.GetOrdinal("LowerValue")), "lower");
        AssertEqual("MIXED", reader.GetString(reader.GetOrdinal("UpperValue")), "upper");
        AssertEqual("bcd", reader.GetString(reader.GetOrdinal("SubstringValue")), "substr");
        AssertEqual(4, reader.GetInt32(reader.GetOrdinal("InStringValue")), "instr");
        AssertEqual(6, reader.GetInt32(reader.GetOrdinal("LengthValue")), "length");
        AssertEqual("padded", reader.GetString(reader.GetOrdinal("TrimValue")), "trim");
        AssertEqual("left", reader.GetString(reader.GetOrdinal("LeftTrimValue")), "ltrim");
        AssertEqual("right", reader.GetString(reader.GetOrdinal("RightTrimValue")), "rtrim");
        AssertEqual("xycxyc", reader.GetString(reader.GetOrdinal("ReplaceValue")), "replace");
        AssertEqual("ABC", reader.GetString(reader.GetOrdinal("CharValue")), "char");
        AssertEqual(65, reader.GetInt32(reader.GetOrdinal("UnicodeValue")), "unicode");
        AssertEqual("fallback", reader.GetString(reader.GetOrdinal("CoalesceValue")), "coalesce");
        AssertEqual("fallback", reader.GetString(reader.GetOrdinal("IfNullValue")), "ifnull");
        if (!await reader.IsDBNullAsync(reader.GetOrdinal("NullIfValue")))
        {
            throw new InvalidOperationException("SQLite function nullif returned non-null; expected null.");
        }
        AssertEqual("yes", reader.GetString(reader.GetOrdinal("IifValue")), "iif");
        AssertEqual("no", reader.GetString(reader.GetOrdinal("IfValue")), "if");
        AssertEqual("ab", reader.GetString(reader.GetOrdinal("ConcatValue")), "concat");
        AssertEqual("a|b", reader.GetString(reader.GetOrdinal("ConcatWsValue")), "concat_ws");
        AssertEqual("0007", reader.GetString(reader.GetOrdinal("PrintfValue")), "printf");
        AssertEqual("item-7", reader.GetString(reader.GetOrdinal("FormatValue")), "format");
        AssertEqual("'quoted'", reader.GetString(reader.GetOrdinal("QuoteValue")), "quote");
        AssertEqual(4, reader.GetInt32(reader.GetOrdinal("ZeroBlobLengthValue")), "zeroblob");
        AssertEqual(2, reader.GetInt32(reader.GetOrdinal("TextOctetLengthValue")), "octet_length text");
        AssertEqual(3, reader.GetInt32(reader.GetOrdinal("BlobOctetLengthValue")), "octet_length blob");
        AssertEqual(8, reader.GetInt32(reader.GetOrdinal("RandomBlobLengthValue")), "randomblob");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("LikelyValue")), "likely");
        AssertEqual(0, reader.GetInt32(reader.GetOrdinal("UnlikelyValue")), "unlikely");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("LikelihoodValue")), "likelihood");
        AssertEqual(12.35, reader.GetDouble(reader.GetOrdinal("RoundValue")), "round");
        AssertEqual(42, reader.GetInt32(reader.GetOrdinal("AbsValue")), "abs");
        AssertClose(Math.Sin(0.5), reader.GetDouble(reader.GetOrdinal("SinValue")), "sin");
        AssertClose(Math.Acos(0.5), reader.GetDouble(reader.GetOrdinal("AcosValue")), "acos");
        AssertClose(Math.Atan2(0.5, 2.0), reader.GetDouble(reader.GetOrdinal("Atan2Value")), "atan2");
        AssertClose(Math.Log(0.5), reader.GetDouble(reader.GetOrdinal("LnValue")), "ln");
        AssertClose(3.0, reader.GetDouble(reader.GetOrdinal("LogBaseValue")), "log base");
        AssertClose(3.0, reader.GetDouble(reader.GetOrdinal("Log2Value")), "log2");
        AssertClose(2.0, reader.GetDouble(reader.GetOrdinal("Log10Value")), "log10");
        AssertEqual(-1, reader.GetInt32(reader.GetOrdinal("SignValue")), "sign");
        AssertEqual(3.0, reader.GetDouble(reader.GetOrdinal("TruncValue")), "trunc");
        AssertClose(Math.PI, reader.GetDouble(reader.GetOrdinal("RadiansValue")), "radians");
        AssertClose(180.0, reader.GetDouble(reader.GetOrdinal("DegreesValue")), "degrees");
        AssertEqual("integer", reader.GetString(reader.GetOrdinal("TypeOfValue")), "typeof");
        var sqliteVersion = reader.GetString(reader.GetOrdinal("SqliteVersionValue"));
        if (string.IsNullOrWhiteSpace(sqliteVersion))
        {
            throw new InvalidOperationException("SQLite function sqlite_version returned an empty value.");
        }
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("SqliteSourceIdPresentValue")), "sqlite_source_id");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("Fts5CompileOptionValue")), "sqlite_compileoption_used");
        AssertEqual(1, reader.GetInt32(reader.GetOrdinal("CompileOptionGetValue")), "sqlite_compileoption_get");
        AssertEqual("0AFF", reader.GetString(reader.GetOrdinal("HexValue")), "hex");
        AssertEqual("0AFF", reader.GetString(reader.GetOrdinal("UnhexValue")), "unhex");
        AssertEqual("E460", reader.GetString(reader.GetOrdinal("SoundexEulerValue")), "soundex Euler");
        AssertEqual("P236", reader.GetString(reader.GetOrdinal("SoundexPfisterValue")), "soundex Pfister");
        AssertEqual("?000", reader.GetString(reader.GetOrdinal("SoundexNoLettersValue")), "soundex no letters");
        AssertEqual("blob", reader.GetString(reader.GetOrdinal("Sha3TypeValue")), "sha3 typeof");
        AssertEqual(
            "3A985DA74FE225B2045C172D6BD390BD855F086E3E9D525B46BFE24511431532",
            reader.GetString(reader.GetOrdinal("Sha3DefaultValue")),
            "sha3 default");
        AssertEqual(
            "3A985DA74FE225B2045C172D6BD390BD855F086E3E9D525B46BFE24511431532",
            reader.GetString(reader.GetOrdinal("Sha3BlobValue")),
            "sha3 blob");
        AssertEqual(28, reader.GetInt32(reader.GetOrdinal("Sha3_224LengthValue")), "sha3 224");
        AssertEqual(48, reader.GetInt32(reader.GetOrdinal("Sha3_384LengthValue")), "sha3 384");
        AssertEqual(64, reader.GetInt32(reader.GetOrdinal("Sha3_512LengthValue")), "sha3 512");
        AssertEqual("AB", reader.GetString(reader.GetOrdinal("UnistrValue")), "unistr");
        AssertEqual("unistr('A\\u000aB')", reader.GetString(reader.GetOrdinal("UnistrQuoteValue")), "unistr_quote");
        AssertEqual("2026-05-24", reader.GetString(reader.GetOrdinal("DateValue")), "strftime");
        AssertEqual(42, reader.GetInt32(reader.GetOrdinal("JsonValue")), "json_extract");

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected only one native function row.");
        }

        return "OK";
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
}
