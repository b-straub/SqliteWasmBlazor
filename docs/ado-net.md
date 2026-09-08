# ADO.NET Usage

SqliteWasmBlazor's ADO.NET provider is complete and usable standalone, without
Entity Framework Core.

## Setup for Non-EF Core Usage

**Program.cs:**

```csharp
using SqliteWasmBlazor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Register SqliteWasm database management service
builder.Services.AddSqliteWasm();

var host = builder.Build();

// Initialize the SqliteWasm worker bridge (no EF Core needed)
await host.Services.InitializeSqliteWasmAsync();

await host.RunAsync();
```

## Using the ADO.NET Provider

SqliteWasmBlazor provides standard ADO.NET classes that work exactly like `Microsoft.Data.Sqlite`:

```csharp
@inject ISqliteWasmDatabaseService DatabaseService
@implements IAsyncDisposable

<h3>Users</h3>

@foreach (var user in users)
{
    <div>@user.Name (@user.Email)</div>
}

@code {
    private SqliteWasmConnection? connection;
    private List<User> users = new();

    protected override async Task OnInitializedAsync()
    {
        // Create connection
        connection = new SqliteWasmConnection("Data Source=MyApp.db");
        await connection.OpenAsync();

        // Create table if not exists
        using var createCmd = connection.CreateCommand();
        createCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Email TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            )";
        await createCmd.ExecuteNonQueryAsync();

        // Query data
        using var queryCmd = connection.CreateCommand();
        queryCmd.CommandText = "SELECT Id, Name, Email FROM Users ORDER BY Name";

        using var reader = await queryCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            users.Add(new User
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Email = reader.GetString(2)
            });
        }
    }

    private async Task AddUser(string name, string email)
    {
        using var cmd = connection!.CreateCommand();
        cmd.CommandText = "INSERT INTO Users (Name, Email, CreatedAt) VALUES ($name, $email, $date)";

        // Add parameters
        cmd.Parameters.Add(new SqliteWasmParameter { ParameterName = "$name", Value = name });
        cmd.Parameters.Add(new SqliteWasmParameter { ParameterName = "$email", Value = email });
        cmd.Parameters.Add(new SqliteWasmParameter { ParameterName = "$date", Value = DateTime.UtcNow.ToString("O") });

        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
        {
            await connection.CloseAsync();
            await connection.DisposeAsync();
        }
    }

    record User
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string Email { get; init; } = "";
    }
}
```

## Using Transactions

```csharp
await using var connection = new SqliteWasmConnection("Data Source=MyApp.db");
await connection.OpenAsync();

// Start transaction
await using var transaction = await connection.BeginTransactionAsync();

try
{
    // Execute multiple commands
    await using var cmd1 = connection.CreateCommand();
    cmd1.Transaction = transaction;
    cmd1.CommandText = "INSERT INTO Users (Name, Email) VALUES ('Alice', 'alice@example.com')";
    await cmd1.ExecuteNonQueryAsync();

    await using var cmd2 = connection.CreateCommand();
    cmd2.Transaction = transaction;
    cmd2.CommandText = "UPDATE Users SET Email = 'newemail@example.com' WHERE Name = 'Bob'";
    await cmd2.ExecuteNonQueryAsync();

    // Commit transaction
    await transaction.CommitAsync();
}
catch
{
    // Rollback on error
    await transaction.RollbackAsync();
    throw;
}
```

## Available ADO.NET Classes

All standard ADO.NET types are implemented:

| Class | Base Type | Purpose |
|-------|-----------|---------|
| `SqliteWasmConnection` | `DbConnection` | Database connection |
| `SqliteWasmCommand` | `DbCommand` | SQL command execution |
| `SqliteWasmDataReader` | `DbDataReader` | Forward-only result reading |
| `SqliteWasmParameter` | `DbParameter` | Query parameters |
| `SqliteWasmTransaction` | `DbTransaction` | Transaction support |

## Key Differences from Microsoft.Data.Sqlite

1. **Use `SqliteWasmConnection` instead of `SqliteConnection`**
   - Same interface, different implementation
   - All operations are async (required for worker communication)

2. **Initialization required**
   - Call `host.Services.InitializeSqliteWasmAsync()` once at startup
   - This initializes the Web Worker and OPFS

3. **Persistence is automatic**
   - All changes are immediately written to OPFS
   - No manual save/load operations needed (unlike in-memory databases)

4. **Database files stored in OPFS**
   - Persistent across browser restarts
   - Isolated per-origin storage
   - Typically several GB quota available

## When to Use ADO.NET vs EF Core

The usual trade, with one WASM-specific wrinkle: EF Core brings migrations,
LINQ and change tracking at the cost of ~1 MB of assemblies you ship to every
visitor. Going without it is a real saving here in a way it is not on a server.

Reach for raw ADO.NET when you are porting existing ADO.NET code, when the
queries are simple enough that a mapper earns nothing, or when you want to
control exactly what SQL runs. Take EF Core when you want the schema managed
for you — `InitializeSqliteWasmDatabaseAsync` plus `<SqliteWasmDatabaseInitializer/>`
applies migrations,
and hand-rolling that is where most of the work would go.

## Database Management via DI

For database management operations (check existence, delete, rename), inject `ISqliteWasmDatabaseService`:

```csharp
@inject ISqliteWasmDatabaseService DatabaseService

@code {
    private async Task ResetDatabaseAsync()
    {
        // Check if database exists
        if (await DatabaseService.ExistsDatabaseAsync("MyApp.db"))
        {
            // Close any open connections first
            await DatabaseService.CloseDatabaseAsync("MyApp.db");

            // Delete the database
            await DatabaseService.DeleteDatabaseAsync("MyApp.db");
        }

        // Recreate...
    }

    private async Task BackupDatabaseAsync(Stream destination)
    {
        // Export, don't rename. A rename onto an occupied name leaves the
        // occupant's slot claimed but unreachable, and it leaves you with one
        // copy under two names rather than a backup.
        await DatabaseService.ExportDatabaseToStreamAsync("MyApp.db", destination);
    }
}
```
