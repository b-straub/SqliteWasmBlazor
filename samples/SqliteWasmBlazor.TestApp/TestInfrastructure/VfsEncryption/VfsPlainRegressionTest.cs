using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Models;
using System.Text;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Guards the non-CryptoSync pass-through contract: when a DB is opened
/// without a key, our VFS fork must behave byte-for-byte like vendor's
/// opfs-sahpool. Uses the existing plaintext <see cref="TodoDbContext"/>,
/// which is registered without an <c>EncryptionKey</c>. After writes,
/// the raw OPFS bytes at offset 0 must still start with the SQLite
/// format-3 magic.
/// </summary>
internal sealed class VfsPlainRegressionTest
{
    private readonly IDbContextFactory<TodoDbContext> _factory;
    private readonly ISqliteWasmDatabaseService _databaseService;
    private readonly IEncryptedSqliteWasmDatabaseService _session;

    private const string TodoDatabaseName = "TestDb.db";
    private static readonly byte[] SqliteMagic =
        Encoding.ASCII.GetBytes("SQLite format 3\0");

    public VfsPlainRegressionTest(
        IDbContextFactory<TodoDbContext> factory,
        ISqliteWasmDatabaseService databaseService,
        IEncryptedSqliteWasmDatabaseService session)
    {
        _factory = factory;
        _databaseService = databaseService;
        _session = session;
    }

    public string Name => "VFS_PlainRegression";

    public async ValueTask<string?> RunTestWithFreshDatabaseAsync()
    {
        // Single-key model: ensure the worker has NO global key before
        // exercising the plain pass-through. ClearEncryptionKey implicitly
        // closes every open DB (including a stale TodoDb under globalKey
        // from a prior encrypted test), so the next xOpen re-stamps
        // file.key=undefined and routes through the vendor plain path.
        await _session.LockAsync();

        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureDeletedAsync();
            await ctx.Database.EnsureCreatedAsync();
        }

        return await RunTestAsync();
    }

    private async Task<string?> RunTestAsync()
    {
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var listId = Guid.NewGuid();
            ctx.TodoLists.Add(new TodoList
            {
                Id = listId,
                Title = "plain-regression-list",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
            ctx.Todos.Add(new Todo { Title = "plain-regression-item", Description = "d1", TodoListId = listId });
            ctx.Todos.Add(new Todo { Title = "plain-regression-item-2", Description = "d2", TodoListId = listId });
            await ctx.SaveChangesAsync();
        }

        var bytes = await _databaseService.ExportDatabaseAsync(TodoDatabaseName);

        if (bytes.Length < 16)
        {
            return $"Exported plain DB too small: {bytes.Length}";
        }

        if (!bytes.AsSpan(0, 16).SequenceEqual(SqliteMagic))
        {
            var head = Convert.ToHexString(bytes.AsSpan(0, 16));
            return $"FAIL: plain DB first 16 bytes are {head}, expected 'SQLite format 3\\0' — pass-through byte-compat broken";
        }

        // SQLite header byte 20 is "bytes of unused space at the end of each
        // page". For plain DBs configured without reserve_bytes, this must
        // be 0 — if it's 28 here, our code accidentally applied reserve to a
        // plain DB.
        if (bytes[20] != 0)
        {
            return $"FAIL: plain DB has reserved_bytes={bytes[20]} at header byte 20, expected 0";
        }

        Console.WriteLine($"[{Name}] Plain DB export starts with 'SQLite format 3', reserved_bytes=0 — pass-through intact");
        return null;
    }
}
