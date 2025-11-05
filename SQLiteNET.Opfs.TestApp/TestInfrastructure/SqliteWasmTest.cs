using Microsoft.EntityFrameworkCore;
using SQLiteNET.Opfs.TestApp.Data;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure;

internal abstract class SqliteWasmTest(IDbContextFactory<TodoDbContext> factory)
{
    public abstract string Name { get; }

    protected IDbContextFactory<TodoDbContext> Factory { get; } = factory;

    public abstract ValueTask<string?> RunTestAsync();
}
