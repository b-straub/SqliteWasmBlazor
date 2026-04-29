using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using SqliteWasmBlazor.Crypto.Testing;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for <see cref="SyncGate"/>: the precondition guard that runs above
/// every other sync step. Sync is blocked unless the sender is a known
/// trusted contact.
/// </summary>
public class SyncGateTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestSyncContext _context;
    private readonly ContactService _contacts;
    private readonly SyncGate _gate;

    public SyncGateTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TestSyncContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new TestSyncContext(options);
        _context.Database.EnsureCreated();
        var crypto = new BouncyCastleCryptoProvider();
        var groupEncryption = new GroupEncryptionService(crypto);
        var declarationSigner = new DeclarationSigner(crypto);
        var groupService = new GroupService(_context, groupEncryption, declarationSigner);
        _contacts = new ContactService(_context, groupService, new RecordingWhitelistPushService());
        _gate = new SyncGate(_contacts);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<TrustedContact> AddContactAsync(string name, string ed25519PublicKey)
    {
        var contact = new TrustedContact
        {
            Id = Guid.NewGuid(),
            Username = name,
            Email = $"{name.ToLowerInvariant()}@test.com",
            X25519PublicKey = Convert.ToBase64String(new byte[32]),
            Ed25519PublicKey = ed25519PublicKey,
            IsAdmin = false,
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.PUBLIC,
            SharingId = CryptoSyncBootstrap.SystemSharingId
        };
        _context.Contacts.Add(contact);
        await _context.SaveChangesAsync();
        return contact;
    }

    [Fact]
    public async Task EnsureSenderTrustedAsync_ReturnsContact_ForKnownSender()
    {
        var senderPk = Convert.ToBase64String(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });
        var added = await AddContactAsync("Alice", senderPk);

        var resolved = await _gate.EnsureSenderTrustedAsync(senderPk);

        Assert.Equal(added.Id, resolved.Id);
        Assert.Equal("Alice", resolved.Username);
    }

    [Fact]
    public async Task EnsureSenderTrustedAsync_Throws_ForUnknownSender()
    {
        var unknownPk = Convert.ToBase64String(new byte[32]);

        var ex = await Assert.ThrowsAsync<SyncRejectedException>(
            () => _gate.EnsureSenderTrustedAsync(unknownPk).AsTask());

        Assert.Contains("not a known contact", ex.Message);
    }

    [Fact]
    public async Task EnsureSenderTrustedAsync_Throws_ForNullOrEmptyKey()
    {
        await Assert.ThrowsAsync<SyncRejectedException>(
            () => _gate.EnsureSenderTrustedAsync(string.Empty).AsTask());
    }

    [Fact]
    public async Task SyncRejectedException_IsAnInvalidOperationException()
    {
        var ex = await Assert.ThrowsAsync<SyncRejectedException>(
            () => _gate.EnsureSenderTrustedAsync(string.Empty).AsTask());
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }
}
