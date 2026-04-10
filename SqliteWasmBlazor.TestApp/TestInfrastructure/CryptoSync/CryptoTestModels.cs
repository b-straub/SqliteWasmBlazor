using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

/// <summary>
/// Test entity for CryptoSync integration tests.
/// Editor can CRUD by default; only Owner can delete.
/// Viewer is read-only at the table level but may flip <c>IsBought</c>
/// via the per-column <c>[AllowUpdate]</c> override.
/// </summary>
[Permissions("Editor", Delete = "Owner")]
public class CryptoTestItem : SyncableEntity
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }

    [AllowUpdate("Viewer")]
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
        SeedAdminBootstrap(modelBuilder);
    }
}
