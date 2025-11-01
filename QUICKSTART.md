# SQLiteNET.Opfs - Quick Start Guide

## 5-Minute Setup

### Step 1: Add Reference
```bash
dotnet add reference path/to/SQLiteNET.Opfs/SQLiteNET.Opfs.csproj
```

### Step 2: Update Program.cs
Replace this:
```csharp
builder.Services.AddDbContext<MyDbContext>(options =>
    options.UseInMemoryDatabase("MyDatabase"));

await builder.Build().RunAsync();
```

With this:
```csharp
using SQLiteNET.Opfs.Extensions;

builder.Services.AddOpfsSqliteDbContext<MyDbContext>();

var host = builder.Build();

// Initialize OPFS
await host.Services.InitializeOpfsAsync();

// Configure and create database
using (var scope = host.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<MyDbContext>();
    await dbContext.ConfigureSqliteForWasmAsync();
    await dbContext.Database.EnsureCreatedAsync();
}

await host.RunAsync();
```

### Step 3: Use Your DbContext (No Changes!)
```csharp
@inject MyDbContext DbContext

@code {
    private async Task LoadData()
    {
        var items = await DbContext.MyEntities.ToListAsync();
    }

    private async Task SaveItem(MyEntity item)
    {
        DbContext.MyEntities.Add(item);
        await DbContext.SaveChangesAsync();
    }
}
```

### Step 4: Test Persistence
1. Run your app: `dotnet run`
2. Add some data
3. Refresh the browser (F5)
4. ✅ Your data is still there!

## That's It!

You now have **persistent, high-performance SQLite storage** in your Blazor WASM app.

## Run the Demo

```bash
cd SQLiteNET.Opfs.Demo
dotnet run
```

Navigate to `/todos` to see it in action.

## Need Help?

See [README.md](README.md) for full documentation.
