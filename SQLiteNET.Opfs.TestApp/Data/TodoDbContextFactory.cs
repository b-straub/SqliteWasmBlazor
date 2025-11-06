using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System.Data.SQLite.Wasm;

namespace SQLiteNET.Opfs.TestApp.Data;

/// <summary>
/// Design-time factory for EF Core migrations support
/// This is only used by dotnet ef commands, not at runtime
/// </summary>
public class TodoDbContextFactory : IDesignTimeDbContextFactory<TodoDbContext>
{
    public TodoDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TodoDbContext>();

        // For design-time migrations, use a simple SQLite connection
        // The actual runtime uses SqliteWasmConnection with OPFS
        var connection = new SqliteWasmConnection("Data Source=:memory:");
        optionsBuilder.UseSqliteWasm(connection);

        return new TodoDbContext(optionsBuilder.Options);
    }
}
