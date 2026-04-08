using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for <see cref="SyncGate"/>: the precondition guard that runs above
/// every other sync step. Sync is entirely blocked unless the sender is a
/// known full-trust contact in the local Contacts table.
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
        _contacts = new ContactService(_context);
        _gate = new SyncGate(_contacts);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<TrustedContact> AddContactAsync(string name, TrustLevel trustLevel, string ed25519PublicKey)
    {
        return await _contacts.AddContactAsync(
            new ContactUserData { Username = name, Email = $"{name.ToLowerInvariant()}@test.com" },
            x25519PublicKey: Convert.ToBase64String(new byte[32]),
            ed25519PublicKey: ed25519PublicKey,
            role: SyncRole.Editor,
            trustLevel: trustLevel,
            direction: TrustDirection.Sent);
    }

    [Fact]
    public async Task EnsureSenderTrustedAsync_ReturnsContact_ForFullTrustSender()
    {
        var senderPk = Convert.ToBase64String(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });
        var added = await AddContactAsync("Alice", TrustLevel.Full, senderPk);

        var resolved = await _gate.EnsureSenderTrustedAsync(senderPk);

        Assert.Equal(added.Id, resolved.Id);
        Assert.Equal("Alice", resolved.Username);
        Assert.Equal(TrustLevel.Full, resolved.TrustLevel);
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
    public async Task EnsureSenderTrustedAsync_Throws_ForMarginalTrustContact()
    {
        var senderPk = Convert.ToBase64String(new byte[32] { 99, 98, 97, 96, 95, 94, 93, 92, 91, 90, 89, 88, 87, 86, 85, 84, 83, 82, 81, 80, 79, 78, 77, 76, 75, 74, 73, 72, 71, 70, 69, 68 });
        await AddContactAsync("Marginal", TrustLevel.Marginal, senderPk);

        var ex = await Assert.ThrowsAsync<SyncRejectedException>(
            () => _gate.EnsureSenderTrustedAsync(senderPk).AsTask());

        Assert.Contains("Marginal", ex.Message);
        Assert.Contains("Full", ex.Message);
    }

    [Fact]
    public async Task EnsureSenderTrustedAsync_Throws_ForNoneTrustContact()
    {
        var senderPk = Convert.ToBase64String(new byte[32] { 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50 });
        await AddContactAsync("None", TrustLevel.None, senderPk);

        await Assert.ThrowsAsync<SyncRejectedException>(
            () => _gate.EnsureSenderTrustedAsync(senderPk).AsTask());
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
        // Sanity check: code that catches InvalidOperationException for sync
        // failures will continue to work; SyncRejectedException is a subtype.
        var ex = await Assert.ThrowsAsync<SyncRejectedException>(
            () => _gate.EnsureSenderTrustedAsync(string.Empty).AsTask());
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }
}
