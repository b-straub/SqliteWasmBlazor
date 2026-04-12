using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Base DbContext for any CryptoSync-enabled application.
/// Provides system tables for contacts, group encryption, permissions, and sync tracking.
/// Domain apps inherit this and add their own DbSets.
/// </summary>
public class CryptoSyncContextBase : DbContext
{
    protected CryptoSyncContextBase(DbContextOptions options) : base(options)
    {
    }

    /// <summary>
    /// Registers the <see cref="CryptoSyncSaveChangesInterceptor"/> on every
    /// <see cref="CryptoSyncContextBase"/> derivative. Domain contexts that
    /// override <see cref="OnConfiguring"/> must call
    /// <c>base.OnConfiguring(optionsBuilder)</c> first to inherit the
    /// auto-metadata pipeline.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.AddInterceptors(new CryptoSyncSaveChangesInterceptor());
    }

    /// <summary>
    /// Returns FK child relations for the given parent table. Overridden by the
    /// generator-emitted partial to provide compile-time FK metadata via
    /// <c>SyncableFkMap</c>. Base implementation returns empty — no FK cascade
    /// if the generator hasn't run.
    /// </summary>
    public virtual (string ChildTable, string FkColumn)[] GetChildFkRelations(string parentTable) => [];

    /// <summary>
    /// Clone a <see cref="SyncableEntity"/> for transfer: copies all domain
    /// properties, remaps FK columns via <paramref name="idMap"/>, leaves
    /// sync metadata (Id, SharingId, SharingScope, UpdatedAt, IsDeleted,
    /// DeletedAt) for the interceptor. Overridden by the generator-emitted
    /// partial with a per-entity-type switch.
    /// </summary>
    public virtual SyncableEntity CloneForTransfer(SyncableEntity source, Dictionary<Guid, Guid> idMap)
        => throw new InvalidOperationException(
            "CloneForTransfer requires a generator-emitted override. " +
            "Ensure the CryptoSync source generator has run for this context.");

    // Contacts
    public DbSet<TrustedContact> Contacts => Set<TrustedContact>();

    // Group encryption & key distribution
    public DbSet<ShareGroup> ShareGroups => Set<ShareGroup>();
    public DbSet<ShareTarget> ShareTargets => Set<ShareTarget>();

    // Permissions (compile-time schema, seeded via HasData)
    public DbSet<SyncPermission> Permissions => Set<SyncPermission>();

    // Schema metadata (seeded by generator, queried by worker at import time)
    public DbSet<ColumnRegistryEntry> ColumnRegistry => Set<ColumnRegistryEntry>();

    // Local-only
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<DeviceSettings> DeviceSettings => Set<DeviceSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TrustedContact>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Ed25519PublicKey).IsUnique();
            entity.HasIndex(e => e.X25519PublicKey).IsUnique();
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<ShareGroup>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.GroupContext).IsUnique();
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<ShareTarget>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ShareGroupId, e.KeyVersion, e.MemberPublicKey }).IsUnique();
            entity.HasIndex(e => e.MemberPublicKey);
            entity.HasOne(e => e.ShareGroup)
                .WithMany()
                .HasForeignKey(e => e.ShareGroupId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.GrantedByContact)
                .WithMany()
                .HasForeignKey(e => e.GrantedByContactId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<SyncPermission>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SharingScope, e.SharingId, e.TableName, e.RecordId, e.Role }).IsUnique();
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<ColumnRegistryEntry>(entity =>
        {
            entity.ToTable("_column_registry");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TableName, e.ColumnIndex }).IsUnique();
        });

        modelBuilder.Entity<SyncState>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<DeviceSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        SeedSystemTablePermissions(modelBuilder);
    }

    private static void SeedSystemTablePermissions(ModelBuilder modelBuilder)
    {
        var systemTables = new[] { "Contacts", "ShareGroups", "ShareTargets" };

        var seeds = new List<SyncPermission>();

        foreach (var table in systemTables)
        {
            // Owner: full CRUD on system tables
            seeds.Add(CreateSystemPermission(table, SyncRole.Owner,
                canInsert: true, canRead: true, canUpdate: true, canDelete: true));
            // Editor/Viewer: read-only on system tables
            seeds.Add(CreateSystemPermission(table, SyncRole.Editor,
                canInsert: false, canRead: true, canUpdate: false, canDelete: false));
            seeds.Add(CreateSystemPermission(table, SyncRole.Viewer,
                canInsert: false, canRead: true, canUpdate: false, canDelete: false));
        }

        modelBuilder.Entity<SyncPermission>().HasData(seeds);
    }

    private static SyncPermission CreateSystemPermission(
        string tableName, SyncRole role,
        bool canInsert, bool canRead, bool canUpdate, bool canDelete)
    {
        return new SyncPermission
        {
            Id = DeterministicGuid($"SystemPermission:{(int)role}:{tableName}"),
            Role = role,
            TableName = tableName,
            CanInsert = canInsert,
            CanRead = canRead,
            CanUpdate = canUpdate,
            CanDelete = canDelete,
            SharingScope = SharingScope.Public,
            SharingId = "system",
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    internal static Guid DeterministicGuid(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        return new Guid(guidBytes);
    }
}
