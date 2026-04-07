using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Base DbContext for any CryptoSync-enabled application.
/// Provides system tables for contacts, invitations, sharing keys, permissions, and sync tracking.
/// Domain apps inherit this and add their own DbSets.
/// </summary>
public abstract class CryptoSyncContextBase : DbContext
{
    protected CryptoSyncContextBase(DbContextOptions options) : base(options)
    {
    }

    // Contacts & trust
    public DbSet<TrustedContact> Contacts => Set<TrustedContact>();
    public DbSet<SentInvitation> SentInvitations => Set<SentInvitation>();
    public DbSet<ReceivedInvitation> ReceivedInvitations => Set<ReceivedInvitation>();

    // Sharing & keys
    public DbSet<SharingKey> SharingKeys => Set<SharingKey>();

    // Permissions (admin-defined, seeded via migration)
    public DbSet<SyncPermission> Permissions => Set<SyncPermission>();

    // Local-only
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<DeviceSettings> DeviceSettings => Set<DeviceSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Contacts (system table, syncable — broadcasts to peers under public scope when Full)
        modelBuilder.Entity<TrustedContact>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Ed25519PublicKey).IsUnique();
            entity.HasIndex(e => e.X25519PublicKey).IsUnique();
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        // Sent invitations
        modelBuilder.Entity<SentInvitation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.InviteCode).IsUnique();
            entity.HasOne(e => e.TrustedContact)
                .WithMany()
                .HasForeignKey(e => e.TrustedContactId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Received invitations
        modelBuilder.Entity<ReceivedInvitation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.InviteCode);
            entity.HasOne(e => e.TrustedContact)
                .WithMany()
                .HasForeignKey(e => e.TrustedContactId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Sharing keys
        modelBuilder.Entity<SharingKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SharingId, e.ClientContactId }).IsUnique();
            entity.HasIndex(e => e.SharingId);
            entity.HasOne(e => e.ClientContact)
                .WithMany()
                .HasForeignKey(e => e.ClientContactId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.GrantedByContact)
                .WithMany()
                .HasForeignKey(e => e.GrantedByContactId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Permissions (soft-delete filtered, seeded via migration)
        modelBuilder.Entity<SyncPermission>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Lookup order at runtime: (Table, RecordId=row.Id) → fall back to (Table, RecordId=NULL)
            // RecordId == null = table-wide rule, non-null = per-row write-lock override.
            entity.HasIndex(e => new { e.SharingScope, e.SharingId, e.TableName, e.RecordId, e.Role }).IsUnique();
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        // Sync state (local only)
        modelBuilder.Entity<SyncState>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        // Device settings (local only)
        modelBuilder.Entity<DeviceSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        // Seed system table permissions:
        // Owner (admin) = full CRUD, Editor/Viewer = Read only
        SeedSystemTablePermissions(modelBuilder);
    }

    /// <summary>
    /// Seeds permissions for system tables. Owner = full CRUD, others = Read only.
    /// Uses deterministic GUIDs derived from role + table name.
    /// </summary>
    private static void SeedSystemTablePermissions(ModelBuilder modelBuilder)
    {
        var systemTables = new[] { "Contacts", "SentInvitations", "ReceivedInvitations", "SharingKeys", "Permissions" };

        var seeds = new List<SyncPermission>();

        foreach (var table in systemTables)
        {
            // Owner: full CRUD
            seeds.Add(CreateSystemPermission(table, SyncRole.Owner, "{}"));

            // Editor: read only
            seeds.Add(CreateSystemPermission(table, SyncRole.Editor,
                $"{{\"{table}\":\"readonly\"}}"));

            // Viewer: read only
            seeds.Add(CreateSystemPermission(table, SyncRole.Viewer,
                $"{{\"{table}\":\"readonly\"}}"));
        }

        modelBuilder.Entity<SyncPermission>().HasData(seeds);
    }

    private static SyncPermission CreateSystemPermission(string tableName, SyncRole role, string permissionDiffJson)
    {
        return new SyncPermission
        {
            Id = DeterministicGuid($"SystemPermission:{(int)role}:{tableName}"),
            Role = role,
            TableName = tableName,
            PermissionDiffJson = permissionDiffJson,
            SharingScope = SharingScope.Public,
            SharingId = "system",
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    /// <summary>
    /// Deterministic GUID derived from a SHA-256 hash of the input string, truncated
    /// to 16 bytes. SHA-256 is used (not MD5) because Blazor WASM's runtime crypto
    /// provider does not ship MD5. The generator at
    /// <c>CryptoSyncGenerator.cs:GeneratePermissionSeedData</c> must produce
    /// byte-identical GUIDs for the same input strings, so both sites use the same
    /// SHA-256-truncated-to-16 scheme.
    /// </summary>
    internal static Guid DeterministicGuid(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        return new Guid(guidBytes);
    }
}
