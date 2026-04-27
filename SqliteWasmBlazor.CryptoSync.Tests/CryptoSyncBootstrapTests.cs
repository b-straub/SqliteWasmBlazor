using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using SqliteWasmBlazor.Crypto.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for <see cref="CryptoSyncBootstrap"/>: pure crypto seed generation.
/// Verifies that <see cref="CryptoSyncBootstrap.CreateAdminSeedAsync"/> produces
/// well-formed seed data with valid wrapped CEK, correct contact fields, and
/// consistent cross-references.
/// </summary>
public class CryptoSyncBootstrapTests
{
    private readonly ICryptoProvider _crypto = new BouncyCastleCryptoProvider();

    private async Task<(AdminSeedData Seed, SqliteWasmBlazor.Crypto.Abstractions.Models.DualKeyPairFull Keys)> CreateSeedAsync()
    {
        var groupEncryption = new GroupEncryptionService(_crypto);
        var bootstrap = new CryptoSyncBootstrap(groupEncryption, new DeclarationSigner(_crypto));

        var adminSeed = new byte[32];
        for (var i = 0; i < 32; i++) { adminSeed[i] = (byte)(i + 1); }
        var keys = await _crypto.DeriveDualKeyPairAsync(adminSeed);

        var seed = await bootstrap.CreateAdminSeedAsync(keys, "Admin", "admin@test.com", "Test Device");
        return (seed, keys);
    }

    [Fact]
    public async Task CreateAdminSeedAsync_AdminContact_HasCorrectFields()
    {
        var (seed, keys) = await CreateSeedAsync();

        Assert.NotEqual(Guid.Empty, seed.AdminContact.Id);
        Assert.Equal("Admin", seed.AdminContact.Username);
        Assert.Equal("admin@test.com", seed.AdminContact.Email);
        Assert.Equal(keys.X25519PublicKey, seed.AdminContact.X25519PublicKey);
        Assert.Equal(keys.Ed25519PublicKey, seed.AdminContact.Ed25519PublicKey);
        Assert.True(seed.AdminContact.IsAdmin);
        Assert.Equal(ContactStatus.Trusted, seed.AdminContact.Status);
        Assert.Equal(SharingScope.PUBLIC, seed.AdminContact.SharingScope);
        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, seed.AdminContact.SharingId);
    }

    [Fact]
    public async Task CreateAdminSeedAsync_SystemGroup_HasCorrectFields()
    {
        var (seed, keys) = await CreateSeedAsync();

        Assert.NotEqual(Guid.Empty, seed.SystemGroup.Id);
        Assert.Equal(CryptoSyncBootstrap.SystemGroupContext, seed.SystemGroup.GroupContext);
        Assert.Equal(1, seed.SystemGroup.KeyVersion);
        Assert.Equal(keys.X25519PublicKey, seed.SystemGroup.GroupAdminPublicKey);
    }

    [Fact]
    public async Task CreateAdminSeedAsync_ShareTarget_ReferencesGroupAndContact()
    {
        var (seed, _) = await CreateSeedAsync();

        Assert.Equal(seed.SystemGroup.Id, seed.AdminShareTarget.ShareGroupId);
        Assert.Equal(seed.AdminContact.Id, seed.AdminShareTarget.GrantedByContactId);
        Assert.Equal(SyncRole.OWNER, seed.AdminShareTarget.Role);
        Assert.True(seed.AdminShareTarget.WrappedContentKey.Length > 12);
    }

    [Fact]
    public async Task CreateAdminSeedAsync_WrappedCek_CanBeUnwrapped()
    {
        var (seed, keys) = await CreateSeedAsync();

        var wrapped = CryptoSyncBootstrap.DeserializeWrappedCek(seed.AdminShareTarget.WrappedContentKey);
        var adminPrivKey = Convert.FromBase64String(keys.X25519PrivateKey);
        var wrappingKeyResult = await _crypto.DeriveWrappingKeyAsync(
            adminPrivKey, seed.SystemGroup.GroupAdminPublicKey, seed.SystemGroup.GroupContext);
        Assert.True(wrappingKeyResult.Success);

        var unwrapResult = await _crypto.UnwrapContentKeyAsync(wrapped, wrappingKeyResult.Value!);
        Assert.True(unwrapResult.Success);
        Assert.Equal(32, unwrapResult.Value!.Length);
    }

    [Fact]
    public async Task CreateAdminSeedAsync_DeviceSettings_LinkedToContact()
    {
        var (seed, _) = await CreateSeedAsync();

        Assert.True(seed.Device.IsAdmin);
        Assert.Equal(seed.AdminContact.Id, seed.Device.AdminContactId);
    }

    [Fact]
    public async Task HasData_Seed_IsAvailableInTestSyncContext()
    {
        // The generated AdminSeed.g.cs provides SeedAdminBootstrap(ModelBuilder)
        // which TestSyncContext calls in OnModelCreating. Verify it applied.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<TestSyncContext>()
            .UseSqlite(connection)
            .Options;
        using var context = new TestSyncContext(options);
        await context.Database.EnsureCreatedAsync();

        var admin = await context.Contacts.SingleAsync(c => c.IsAdmin);
        Assert.Equal("TestAdmin", admin.Username);
        Assert.Equal(ContactStatus.Trusted, admin.Status);

        // Bootstrap now seeds the system group AND the admin's self-group.
        var groups = await context.ShareGroups.OrderBy(g => g.GroupContext).ToListAsync();
        Assert.Equal(2, groups.Count);
        var systemGroup = groups.Single(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        var selfGroup = groups.Single(g => g.GroupContext == CryptoSyncBootstrap.BuildSelfGroupContext(admin.Id));
        Assert.Equal(SharingScope.PUBLIC, systemGroup.SharingScope);
        Assert.Equal(SharingScope.CLIENT, selfGroup.SharingScope);
        // Both ShareGroup rows route via the system CEK on the wire.
        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, systemGroup.SharingId);
        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, selfGroup.SharingId);

        var targets = await context.ShareTargets.ToListAsync();
        Assert.Equal(2, targets.Count);
        var systemTarget = targets.Single(t => t.ShareGroupId == systemGroup.Id);
        var selfTarget = targets.Single(t => t.ShareGroupId == selfGroup.Id);
        Assert.True(systemTarget.WrappedContentKey.Length > 12);
        Assert.True(selfTarget.WrappedContentKey.Length > 12);
        Assert.Equal(SyncRole.OWNER, systemTarget.Role);
        Assert.Equal(SyncRole.OWNER, selfTarget.Role);
        Assert.Equal(admin.X25519PublicKey, systemTarget.MemberPublicKey);
        Assert.Equal(admin.X25519PublicKey, selfTarget.MemberPublicKey);

        var device = await context.DeviceSettings.SingleAsync();
        Assert.True(device.IsAdmin);
        Assert.Equal(admin.Id, device.AdminContactId);
        Assert.Equal(admin.Id, device.OwnContactId);
    }
}
