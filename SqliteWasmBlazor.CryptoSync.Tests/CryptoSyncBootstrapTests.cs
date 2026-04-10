using BlazorPRF.Crypto.Abstractions;
using BlazorPRF.Crypto.Abstractions.Services;
using BlazorPRF.Crypto.Testing;
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

    private async Task<(AdminSeedData Seed, BlazorPRF.Crypto.Abstractions.Models.DualKeyPairFull Keys)> CreateSeedAsync()
    {
        var groupEncryption = new GroupEncryptionService(_crypto);
        var bootstrap = new CryptoSyncBootstrap(groupEncryption);

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
        Assert.True(seed.AdminContact.IsTrusted);
        Assert.Equal(SharingScope.Public, seed.AdminContact.SharingScope);
        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, seed.AdminContact.SharingId);
    }

    [Fact]
    public async Task CreateAdminSeedAsync_SystemGroup_HasCorrectFields()
    {
        var (seed, keys) = await CreateSeedAsync();

        Assert.NotEqual(Guid.Empty, seed.SystemGroup.Id);
        Assert.Equal(CryptoSyncBootstrap.SystemGroupContext, seed.SystemGroup.GroupContext);
        Assert.Equal(1, seed.SystemGroup.KeyVersion);
        Assert.Equal(keys.X25519PublicKey, seed.SystemGroup.AdminPublicKey);
    }

    [Fact]
    public async Task CreateAdminSeedAsync_ShareTarget_ReferencesGroupAndContact()
    {
        var (seed, _) = await CreateSeedAsync();

        Assert.Equal(seed.SystemGroup.Id, seed.AdminShareTarget.ShareGroupId);
        Assert.Equal(seed.AdminContact.Id, seed.AdminShareTarget.GrantedByContactId);
        Assert.Equal(SyncRole.Owner, seed.AdminShareTarget.Role);
        Assert.True(seed.AdminShareTarget.WrappedContentKey.Length > 12);
    }

    [Fact]
    public async Task CreateAdminSeedAsync_WrappedCek_CanBeUnwrapped()
    {
        var (seed, keys) = await CreateSeedAsync();

        var wrapped = CryptoSyncBootstrap.DeserializeWrappedCek(seed.AdminShareTarget.WrappedContentKey);
        var adminPrivKey = Convert.FromBase64String(keys.X25519PrivateKey);
        var wrappingKeyResult = await _crypto.DeriveWrappingKeyAsync(
            adminPrivKey, seed.SystemGroup.AdminPublicKey, seed.SystemGroup.GroupContext);
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
        Assert.True(admin.IsTrusted);

        var group = await context.ShareGroups.SingleAsync();
        Assert.Equal(CryptoSyncBootstrap.SystemGroupContext, group.GroupContext);

        var target = await context.ShareTargets.SingleAsync();
        Assert.Equal(group.Id, target.ShareGroupId);
        Assert.True(target.WrappedContentKey.Length > 12);

        var device = await context.DeviceSettings.SingleAsync();
        Assert.True(device.IsAdmin);
        Assert.Equal(admin.Id, device.AdminContactId);
    }
}
