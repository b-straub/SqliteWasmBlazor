using BlazorPRF.Crypto.Abstractions;
using BlazorPRF.Crypto.Abstractions.Models;
using BlazorPRF.Crypto.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for <see cref="CryptoSyncBootstrap"/>: the first-launch admin
/// scaffolding. Verifies the post-bootstrap state is correct (DeviceSettings
/// flagged admin, admin's TrustedContact present at Full trust in the
/// system scope, admin's self-SharingKey present and well-formed) and that
/// re-running the bootstrap is idempotent.
/// </summary>
public class CryptoSyncBootstrapTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestSyncContext _context = null!;
    private CryptoSyncBootstrap _bootstrap = null!;
    private ICryptoProvider _crypto = null!;
    private DualKeyPairFull _adminKeys = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TestSyncContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new TestSyncContext(options);
        await _context.Database.EnsureCreatedAsync();

        _crypto = new BouncyCastleCryptoProvider();
        _bootstrap = new CryptoSyncBootstrap(_context, _crypto);

        // Derive a deterministic admin key pair from a fixed seed for test reproducibility.
        var adminSeed = new byte[32];
        for (var i = 0; i < adminSeed.Length; i++) adminSeed[i] = (byte)(i + 1);
        _adminKeys = await _crypto.DeriveDualKeyPairAsync(adminSeed);
    }

    public Task DisposeAsync()
    {
        _context.Dispose();
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private async Task<TrustedContact> RunBootstrapAsync(string username = "Admin")
    {
        return await _bootstrap.InitializeAdminAsync(
            _adminKeys, username, $"{username.ToLowerInvariant()}@test.com", "Test Device");
    }

    [Fact]
    public async Task InitializeAdminAsync_CreatesAdminContact_WithIsAdminMarker()
    {
        var admin = await RunBootstrapAsync();

        Assert.NotEqual(Guid.Empty, admin.Id);
        Assert.Equal("Admin", admin.Username);
        Assert.Equal(_adminKeys.X25519PublicKey, admin.X25519PublicKey);
        Assert.Equal(_adminKeys.Ed25519PublicKey, admin.Ed25519PublicKey);
        Assert.Equal(SyncRole.Owner, admin.Role);
    }

    [Fact]
    public async Task InitializeAdminAsync_AdminContact_IsFullTrust()
    {
        var admin = await RunBootstrapAsync();
        Assert.Equal(TrustLevel.Full, admin.TrustLevel);
    }

    [Fact]
    public async Task InitializeAdminAsync_AdminContact_IsInPublicSystemScope()
    {
        // The admin's contact row sits in the public system scope so peers
        // learn about admin once they're added to the system scope.
        var admin = await RunBootstrapAsync();
        Assert.Equal(SharingScope.Public, admin.SharingScope);
        Assert.Equal(KeyDerivation.SystemSharingId, admin.SharingId);
    }

    [Fact]
    public async Task InitializeAdminAsync_CreatesDeviceSettings_WithIsAdminTrue()
    {
        var admin = await RunBootstrapAsync();

        var device = await _context.DeviceSettings.SingleAsync();
        Assert.True(device.IsAdmin);
        Assert.Equal("Test Device", device.DeviceName);
        Assert.Equal(admin.Id, device.AdminContactId);
    }

    [Fact]
    public async Task InitializeAdminAsync_CreatesSelfSharingKey_ForSystemScope()
    {
        var admin = await RunBootstrapAsync();

        var sharingKey = await _context.SharingKeys.SingleAsync();
        Assert.Equal(KeyDerivation.SystemSharingId, sharingKey.SharingId);
        Assert.Equal(SharingScope.Public, sharingKey.SharingScope);
        Assert.Equal(admin.Id, sharingKey.ClientContactId);
        Assert.Equal(admin.Id, sharingKey.GrantedByContactId);
        Assert.Equal(SyncRole.Owner, sharingKey.Role);
        Assert.NotNull(sharingKey.WrappedContentKey);
        Assert.NotEmpty(sharingKey.WrappedContentKey);
    }

    [Fact]
    public async Task InitializeAdminAsync_SelfSharingKey_UnwrapsToDerivedSystemKey()
    {
        // The wrapped content key on the admin's self-SharingKey, when
        // unwrapped with the admin's X25519 private key, must yield the SAME
        // 32 bytes that KeyDerivation.DeriveSystemContentKey produces.
        // This is the integrity check that "stored wrap" and "deterministic
        // derivation" agree on the system content key (decisions §15 + §16).
        await RunBootstrapAsync();

        var sharingKey = await _context.SharingKeys.SingleAsync();
        var encryptedMessage = EnvelopeBytes.Deserialize(sharingKey.WrappedContentKey);

        var unwrapResult = await _crypto.DecryptAsymmetricAsync(
            encryptedMessage,
            Convert.FromBase64String(_adminKeys.X25519PrivateKey));
        Assert.True(unwrapResult.Success);

        var unwrappedKey = Convert.FromBase64String(unwrapResult.Value!);
        var derivedKey = KeyDerivation.DeriveSystemContentKey(
            Convert.FromBase64String(_adminKeys.X25519PrivateKey));

        Assert.Equal(derivedKey, unwrappedKey);
    }

    [Fact]
    public async Task InitializeAdminAsync_Idempotent_DoesNotDuplicateRows()
    {
        var first = await RunBootstrapAsync();
        var second = await RunBootstrapAsync("Admin");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await _context.Contacts.CountAsync());
        Assert.Equal(1, await _context.DeviceSettings.CountAsync());
        Assert.Equal(1, await _context.SharingKeys.CountAsync());
    }

    [Fact]
    public async Task InitializeAdminAsync_GateAcceptsAdminAfterBootstrap()
    {
        // End-to-end check: after bootstrap, the SyncGate must consider the
        // admin's own Ed25519 public key a valid full-trust sender. This is
        // the foundational invariant for self-syncing across multiple
        // admin devices later.
        var admin = await RunBootstrapAsync();
        var gate = new SyncGate(new ContactService(_context));

        var resolved = await gate.EnsureSenderTrustedAsync(_adminKeys.Ed25519PublicKey);

        Assert.Equal(admin.Id, resolved.Id);
    }
}
