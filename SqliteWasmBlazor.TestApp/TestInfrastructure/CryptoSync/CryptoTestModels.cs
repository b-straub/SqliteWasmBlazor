using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

/// <summary>
/// Test entity for CryptoSync integration tests.
/// [SyncPermission] seeds Viewer as readonly with IsBought override.
/// </summary>
[SyncPermission(SyncRole.Viewer, "readonly", ReadWriteColumns = ["IsBought"])]
public class CryptoTestItem : SyncableEntity
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public bool IsBought { get; set; }
}

/// <summary>
/// Test DbContext for CryptoSync integration tests.
/// Generator creates Crypto_CryptoTestItem + EF config + registry + permission seed.
/// </summary>
public partial class CryptoTestContext : CryptoSyncContextBase
{
    public CryptoTestContext(DbContextOptions<CryptoTestContext> options) : base(options) { }

    public DbSet<CryptoTestItem> CryptoTestItems => Set<CryptoTestItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ConfigureCryptoTables(modelBuilder);
        SeedPermissions(modelBuilder);
    }
}
