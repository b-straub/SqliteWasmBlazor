using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Test entity using SyncableEntity base class.
/// </summary>
public class TestItem : SyncableEntity
{
    public string Title { get; set; } = "";
    public bool IsCompleted { get; set; }
}

/// <summary>
/// Test context that inherits CryptoSyncContextBase — simulates a real app.
/// Marked partial so the source generator can extend it with
/// <c>ConfigureCryptoTables</c> (shadow-table EF config).
/// </summary>
public partial class TestSyncContext : CryptoSyncContextBase
{
    public TestSyncContext(DbContextOptions options) : base(options) { }
    public DbSet<TestItem> TestItems => Set<TestItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ConfigureCryptoTables(modelBuilder);
        SeedAdminBootstrap(modelBuilder);
    }
}

public class ContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestSyncContext _context;

    public ContextTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TestSyncContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new TestSyncContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Contact_CRUD()
    {
        var contact = new TrustedContact
        {
            Id = Guid.NewGuid(),
            Username = "Alice",
            Email = "alice@test.com",
            X25519PublicKey = Convert.ToBase64String(new byte[32]),
            Ed25519PublicKey = Convert.ToBase64String(new byte[32]),
            IsAdmin = true,
            IsTrusted = true,
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.Public,
            SharingId = "system"
        };

        _context.Contacts.Add(contact);
        await _context.SaveChangesAsync();

        var loaded = await _context.Contacts.FindAsync(contact.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Alice", loaded.Username);
        Assert.True(loaded.IsAdmin);
        Assert.True(loaded.IsTrusted);
    }
}
