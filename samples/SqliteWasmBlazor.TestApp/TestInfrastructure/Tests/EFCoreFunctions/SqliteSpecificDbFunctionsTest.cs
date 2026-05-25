using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

internal class SqliteSpecificDbFunctionsTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_SqliteSpecificDbFunctions";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        context.TypeTests.AddRange(
            new TypeTestEntity
            {
                Id = 1,
                StringValue = "Alpha-One",
                NullableStringValue = "00-01-02-FF",
                BlobValue = [0x00, 0x01, 0x02, 0xFF]
            },
            new TypeTestEntity
            {
                Id = 2,
                StringValue = "bravo-two",
                NullableStringValue = "10203040",
                BlobValue = [0x10, 0x20, 0x30, 0x40]
            },
            new TypeTestEntity
            {
                Id = 3,
                StringValue = "Charlie-Three",
                NullableStringValue = "not hex",
                BlobValue = [0xAA, 0xBB, 0xCC, 0xDD]
            },
            new TypeTestEntity
            {
                Id = 4,
                StringValue = "Delta-Four",
                NullableStringValue = "0-0"
            });
        await context.SaveChangesAsync();

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => EF.Functions.Glob(e.StringValue, "[AC]*-*"))
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 3],
            "EF.Functions.Glob");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => EF.Functions.Collate(e.StringValue, "NOCASE") == "BRAVO-TWO")
                .Select(e => e.Id),
            [2],
            "EF.Functions.Collate");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => EF.Functions.Hex(e.BlobValue!) == "000102FF")
                .Select(e => e.Id),
            [1],
            "EF.Functions.Hex");

        var expectedBlob = new byte[] { 0x00, 0x01, 0x02, 0xFF };
        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.BlobValue!.Contains((byte)0xFF))
                .Select(e => e.Id),
            [1],
            "byte[].Contains");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.BlobValue!.Length == 4)
                .OrderBy(e => e.Id)
                .Select(e => e.Id),
            [1, 2, 3],
            "byte[].Length");

        await AssertSequenceAsync(
            context.TypeTests
                .Where(e => e.BlobValue!.SequenceEqual(expectedBlob))
                .Select(e => e.Id),
            [1],
            "byte[].SequenceEqual");

        var projection = await context.TypeTests
            .Where(e => e.Id == 1)
            .Select(e => new
            {
                Decoded = EF.Functions.Unhex(e.NullableStringValue!, "-"),
                Tail = EF.Functions.Substr(e.BlobValue!, 2),
                Window = EF.Functions.Substr(e.BlobValue!, 2, 2),
                LastByte = EF.Functions.Substr(e.BlobValue!, -1)
            })
            .SingleAsync();

        var decodedPlain = await context.TypeTests
            .Where(e => e.Id == 2)
            .Select(e => EF.Functions.Unhex(e.NullableStringValue!))
            .SingleAsync();

        var invalidDecoded = await context.TypeTests
            .Where(e => e.Id == 3)
            .Select(e => EF.Functions.Unhex(e.NullableStringValue!))
            .SingleAsync();

        var splitPairDecoded = await context.TypeTests
            .Where(e => e.Id == 4)
            .Select(e => EF.Functions.Unhex(e.NullableStringValue!, "-"))
            .SingleAsync();

        AssertBytes(projection.Decoded, [0x00, 0x01, 0x02, 0xFF], "Unhex with ignored separators");
        AssertBytes(decodedPlain, [0x10, 0x20, 0x30, 0x40], "Unhex plain");
        if (invalidDecoded is not null)
        {
            throw new InvalidOperationException("Invalid unhex input should return null.");
        }
        if (splitPairDecoded is not null)
        {
            throw new InvalidOperationException("Unhex input with a split byte pair should return null.");
        }

        AssertBytes(projection.Tail, [0x01, 0x02, 0xFF], "Substr tail");
        AssertBytes(projection.Window, [0x01, 0x02], "Substr window");
        AssertBytes(projection.LastByte, [0xFF], "Substr negative index");

        return "OK";
    }

    private static async Task AssertSequenceAsync(
        IQueryable<int> query,
        int[] expected,
        string operation)
    {
        var actual = await query.ToListAsync();
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"{operation} failed: expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}].");
        }
    }

    private static void AssertBytes(byte[]? actual, byte[] expected, string operation)
    {
        if (actual is null || !actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"{operation} failed: expected [{Convert.ToHexString(expected)}], " +
                $"got [{(actual is null ? "null" : Convert.ToHexString(actual))}].");
        }
    }
}
