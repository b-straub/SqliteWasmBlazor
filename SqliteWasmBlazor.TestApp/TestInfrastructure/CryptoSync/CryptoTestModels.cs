using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

/// <summary>
/// Test entity for CryptoSync integration tests. Phase C will reintroduce
/// permission attributes via the canonical <c>[Permissions]</c> shape once the
/// generator lowering is rewritten — see Phase A finding A1.
/// </summary>
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
        // SeedPermissions(modelBuilder) — generator emits this only when entities carry
        // permission attributes. Phase C will reintroduce the canonical [Permissions]
        // attribute and re-enable this call.
    }
}
