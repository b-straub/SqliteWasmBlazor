using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.VfsEncryption;

/// <summary>
/// Plain entity used exclusively by the PRF-VFS integration tests. Uses
/// recognizable literal strings in its columns so the on-disk-ciphertext
/// test can scan raw bytes and confirm the plaintext never surfaces.
/// </summary>
public class VfsTestItem
{
    public int Id { get; set; }
    public string Marker { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
}

/// <summary>
/// Dedicated DbContext for the PRF-VFS test suite. Intentionally a plain
/// <see cref="DbContext"/> — not a <c>CryptoSyncContextBase</c> — so the
/// tests exercise the VFS in isolation from the full sync stack.
/// </summary>
public sealed class EncryptedTestContext : DbContext
{
    public EncryptedTestContext(DbContextOptions<EncryptedTestContext> options) : base(options) { }

    public DbSet<VfsTestItem> Items => Set<VfsTestItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<VfsTestItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("VfsTestItems");
        });
    }
}

/// <summary>
/// Structural twin of <see cref="EncryptedTestContext"/> that opens through
/// the VFS <i>without</i> a key. Used by the VFS perf tests so the
/// plain-vs-encrypted comparison runs on an identical schema — the ratio
/// then reflects AEAD cost alone, not schema-complexity differences.
/// </summary>
public sealed class PlainVfsTestContext : DbContext
{
    public const string DatabaseName = "PlainVfsTestDb.db";

    public PlainVfsTestContext(DbContextOptions<PlainVfsTestContext> options) : base(options) { }

    public DbSet<VfsTestItem> Items => Set<VfsTestItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<VfsTestItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("VfsTestItems");
        });
    }
}

/// <summary>
/// Context backing the PRF-VFS demo page. Registered with no key in DI:
/// the key flows into the worker-wide global-key registry after the PRF
/// ceremony completes, and subsequent page I/O reads that key dynamically.
/// </summary>
public sealed class PrfVfsTestContext : DbContext
{
    public const string DatabaseName = "PrfVfsTestDb.db";

    public PrfVfsTestContext(DbContextOptions<PrfVfsTestContext> options) : base(options) { }

    public DbSet<VfsTestItem> Items => Set<VfsTestItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<VfsTestItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("VfsTestItems");
        });
    }
}
