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
/// </summary>
public class TestSyncContext : CryptoSyncContextBase
{
    public TestSyncContext(DbContextOptions options) : base(options) { }
    public DbSet<TestItem> TestItems => Set<TestItem>();
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
            Role = SyncRole.Owner,
            TrustLevel = TrustLevel.Full,
            Direction = TrustDirection.Sent,
            VerifiedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.Public,
            SharingId = "system"
        };

        _context.Contacts.Add(contact);
        await _context.SaveChangesAsync();

        var loaded = await _context.Contacts.FindAsync(contact.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Alice", loaded.Username);
        Assert.Equal(SyncRole.Owner, loaded.Role);
    }

    [Fact]
    public async Task SentInvitation_LinkedToContact()
    {
        var contact = new TrustedContact
        {
            Id = Guid.NewGuid(),
            Username = "Bob",
            Email = "bob@test.com",
            X25519PublicKey = Convert.ToBase64String(new byte[32]),
            Ed25519PublicKey = Convert.ToBase64String(new byte[32]),
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.Client,
            SharingId = string.Empty
        };

        var invite = new SentInvitation
        {
            Id = Guid.NewGuid(),
            InviteCode = "INV-test1234",
            Email = "bob@test.com",
            ArmoredInvite = "armored",
            Status = InviteStatus.Accepted,
            CreatedAt = DateTime.UtcNow,
            AcceptedAt = DateTime.UtcNow,
            TrustedContactId = contact.Id
        };

        _context.Contacts.Add(contact);
        _context.SentInvitations.Add(invite);
        await _context.SaveChangesAsync();

        var loaded = await _context.SentInvitations
            .Include(i => i.TrustedContact)
            .FirstAsync(i => i.Id == invite.Id);

        Assert.Equal(InviteStatus.Accepted, loaded.Status);
        Assert.NotNull(loaded.TrustedContact);
    }

    [Fact]
    public async Task Permission_SoftDelete_Filtered()
    {
        var active = new SyncPermission
        {
            Id = Guid.NewGuid(),
            Role = SyncRole.Viewer,
            TableName = "ShoppingItems",
            PermissionDiffJson = "{\"ShoppingItems\":\"readonly\",\"ShoppingItems.IsBought\":\"readwrite\"}",
            UpdatedAt = DateTime.UtcNow
        };

        var deleted = new SyncPermission
        {
            Id = Guid.NewGuid(),
            Role = SyncRole.Viewer,
            TableName = "OldTable",
            PermissionDiffJson = "{}",
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Permissions.Add(active);
        _context.Permissions.Add(deleted);
        await _context.SaveChangesAsync();

        // Query filter should exclude deleted — seed data + active, NOT deleted
        var permissions = await _context.Permissions.ToListAsync();
        Assert.Contains(permissions, p => p.TableName == "ShoppingItems");
        Assert.DoesNotContain(permissions, p => p.TableName == "OldTable");
    }

    [Fact]
    public async Task DeviceSettings_Singleton()
    {
        var settings = new DeviceSettings
        {
            Id = Guid.NewGuid(),
            ClientGuid = Guid.NewGuid().ToString(),
            DeviceName = "Test Device",
            CredentialId = "cred-hint-base64"
        };

        _context.DeviceSettings.Add(settings);
        await _context.SaveChangesAsync();

        var loaded = await _context.DeviceSettings.FirstAsync();
        Assert.Equal("Test Device", loaded.DeviceName);
    }

    [Fact]
    public async Task Contact_UniquePublicKey()
    {
        var pk = Convert.ToBase64String(new byte[32]);

        _context.Contacts.Add(new TrustedContact
        {
            Id = Guid.NewGuid(),
            Username = "First",
            Email = "first@test.com",
            X25519PublicKey = pk,
            Ed25519PublicKey = pk,
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.Client,
            SharingId = string.Empty
        });
        await _context.SaveChangesAsync();

        _context.Contacts.Add(new TrustedContact
        {
            Id = Guid.NewGuid(),
            Username = "Second",
            Email = "second@test.com",
            X25519PublicKey = pk, // duplicate
            Ed25519PublicKey = pk,
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.Client,
            SharingId = string.Empty
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
    }
}
