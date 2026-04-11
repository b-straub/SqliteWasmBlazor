using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.CryptoSync;

/// <summary>
/// Flat test entity — used by the single-table integration tests
/// (WorkerEncryptedRoundTrip, PermissionEnforcement, SchemaVersionMismatch,
/// CryptoBenchmark). Editor can CRUD by default; only Owner can delete.
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
/// Parent entity for the List → Items FK scenario used by multi-table
/// round-trip, sharing, and rotate integration tests. One <c>CryptoTestList</c>
/// owns zero-or-more <c>CryptoTestListItem</c>s via <see cref="CryptoTestListItem.ListId"/>.
/// </summary>
[Permissions("Editor", Delete = "Owner")]
public class CryptoTestList : SyncableEntity
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Navigation — child items belonging to this list.</summary>
    public ICollection<CryptoTestListItem> Items { get; set; } = new List<CryptoTestListItem>();
}

/// <summary>
/// Child entity for the List → Items FK scenario. Owns its own SharingId
/// (inherited from <see cref="SyncableEntity"/>) — the SharingService
/// keeps it in sync with its parent <see cref="CryptoTestList"/> via the
/// FK walk so sharing a list automatically shares every item under it.
/// </summary>
[Permissions("Editor", Delete = "Owner")]
public class CryptoTestListItem : SyncableEntity
{
    public Guid ListId { get; set; }
    public CryptoTestList? List { get; set; }

    public string ItemName { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
}

/// <summary>
/// Test DbContext for CryptoSync integration tests.
/// Generator creates Crypto_* shadow tables + EF config + registry + permission seeds
/// for every <see cref="SyncableEntity"/> reachable from DbSets on this context.
/// </summary>
public partial class CryptoTestContext : CryptoSyncContextBase
{
    public CryptoTestContext(DbContextOptions<CryptoTestContext> options) : base(options) { }

    public DbSet<CryptoTestItem> CryptoTestItems => Set<CryptoTestItem>();
    public DbSet<CryptoTestList> CryptoTestLists => Set<CryptoTestList>();
    public DbSet<CryptoTestListItem> CryptoTestListItems => Set<CryptoTestListItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Parent/child FK: CryptoTestListItem.ListId → CryptoTestList.Id.
        // Restrict delete so SharingService can walk the dependency graph
        // explicitly rather than relying on cascade semantics.
        modelBuilder.Entity<CryptoTestListItem>(entity =>
        {
            entity.HasOne(i => i.List)
                .WithMany(l => l.Items)
                .HasForeignKey(i => i.ListId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        ConfigureCryptoTables(modelBuilder);
        SeedPermissions(modelBuilder);
        SeedAdminBootstrap(modelBuilder);
    }
}
