using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SqliteWasm.Data.Models;

/// <summary>
/// Design-time factory for EF Core migrations.
/// Uses standard Microsoft.Data.Sqlite (NOT SqliteWasmConnection) because:
/// - No browser/worker required at design-time
/// - EF tools only need to inspect the model
/// - Runtime uses SqliteWasmConnection with OPFS
/// </summary>
public class TodoDbContextFactory : IDesignTimeDbContextFactory<TodoDbContext>
{
    public TodoDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TodoDbContext>();

        // Use standard SQLite for design-time (no WASM, no worker, no browser)
        optionsBuilder.UseSqlite("Data Source=:memory:");

        return new TodoDbContext(optionsBuilder.Options);
    }
}
