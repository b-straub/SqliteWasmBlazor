using Microsoft.EntityFrameworkCore;
using SQLiteNET.Opfs.TestApp.Data;
using SQLiteNET.Opfs.TestApp.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.TypeMarshalling;

internal class BinaryDataLargeBlobTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "BinaryData_LargeBlob";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var largeBlob = new byte[1024 * 100]; // 100KB
        new Random(42).NextBytes(largeBlob);

        var entity = new TypeTestEntity { BlobValue = largeBlob };

        context.TypeTests.Add(entity);
        await context.SaveChangesAsync();

        var retrieved = await context.TypeTests.FindAsync(entity.Id);
        if (retrieved is null)
        {
            throw new InvalidOperationException("Failed to retrieve entity");
        }

        if (retrieved.BlobValue is null)
        {
            throw new InvalidOperationException("BlobValue is null");
        }

        if (retrieved.BlobValue.Length != largeBlob.Length)
        {
            throw new InvalidOperationException("BlobValue length mismatch");
        }

        if (!retrieved.BlobValue.SequenceEqual(largeBlob))
        {
            throw new InvalidOperationException("BlobValue content mismatch");
        }

        return "OK";
    }
}
