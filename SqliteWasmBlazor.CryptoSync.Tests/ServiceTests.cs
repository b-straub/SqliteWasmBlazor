using BlazorPRF.Crypto.Abstractions;
using BlazorPRF.Crypto.Abstractions.Models;
using BlazorPRF.Crypto.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

public class ServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestSyncContext _context;
    private readonly ICryptoProvider _crypto = new BouncyCastleCryptoProvider();

    public ServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<TestSyncContext>().UseSqlite(_connection).Options;
        _context = new TestSyncContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<(DualKeyPairFull Alice, DualKeyPairFull Bob, DualKeyPairFull Tom)> CreateKeysAsync()
    {
        var s1 = new byte[32]; Random.Shared.NextBytes(s1);
        var s2 = new byte[32]; Random.Shared.NextBytes(s2);
        var s3 = new byte[32]; Random.Shared.NextBytes(s3);
        return (await _crypto.DeriveDualKeyPairAsync(s1),
                await _crypto.DeriveDualKeyPairAsync(s2),
                await _crypto.DeriveDualKeyPairAsync(s3));
    }

    private byte[] RandomKey()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        return key;
    }

    // ============================================================
    // CONTACT SERVICE
    // ============================================================

    [Fact]
    public async Task ContactService_AddAndRetrieve()
    {
        var (alice, _, _) = await CreateKeysAsync();
        var svc = new ContactService(_context, _crypto);
        var encKey = RandomKey();

        var contact = await svc.AddContactAsync(
            new ContactUserData { Username = "Alice", Email = "alice@test.com" },
            alice.X25519PublicKey, alice.Ed25519PublicKey,
            SyncRole.Owner, TrustLevel.Full, TrustDirection.Sent, encKey);

        var loaded = await svc.GetByEd25519PublicKeyAsync(alice.Ed25519PublicKey);
        Assert.NotNull(loaded);
        Assert.Equal(SyncRole.Owner, loaded.Role);
    }

    [Fact]
    public async Task ContactService_EncryptedUserData_RoundTrip()
    {
        var (alice, _, _) = await CreateKeysAsync();
        var svc = new ContactService(_context, _crypto);
        var encKey = RandomKey();

        await svc.AddContactAsync(
            new ContactUserData { Username = "Alice", Email = "alice@test.com", Comment = "Admin user" },
            alice.X25519PublicKey, alice.Ed25519PublicKey,
            SyncRole.Owner, TrustLevel.Full, TrustDirection.Sent, encKey);

        var contacts = await svc.GetAllWithUserDataAsync(encKey);
        Assert.Single(contacts);
        Assert.Equal("Alice", contacts[0].UserData.Username);
        Assert.Equal("alice@test.com", contacts[0].UserData.Email);
        Assert.Equal("Admin user", contacts[0].UserData.Comment);
    }

    [Fact]
    public async Task ContactService_GetRecipientPublicKeys()
    {
        var (alice, bob, _) = await CreateKeysAsync();
        var svc = new ContactService(_context, _crypto);
        var encKey = RandomKey();

        await svc.AddContactAsync(
            new ContactUserData { Username = "Alice", Email = "a@t.com" },
            alice.X25519PublicKey, alice.Ed25519PublicKey,
            SyncRole.Owner, TrustLevel.Full, TrustDirection.Sent, encKey);
        await svc.AddContactAsync(
            new ContactUserData { Username = "Bob", Email = "b@t.com" },
            bob.X25519PublicKey, bob.Ed25519PublicKey,
            SyncRole.Editor, TrustLevel.Full, TrustDirection.Received, encKey);

        var pks = await svc.GetRecipientPublicKeysAsync();
        Assert.Equal(2, pks.Length);
        Assert.Contains(alice.X25519PublicKey, pks);
        Assert.Contains(bob.X25519PublicKey, pks);
    }

    // ============================================================
    // PERMISSION SERVICE
    // ============================================================

    [Fact]
    public async Task PermissionService_FullAccessByDefault()
    {
        var (alice, _, _) = await CreateKeysAsync();
        var contactSvc = new ContactService(_context, _crypto);
        var permSvc = new PermissionService(_context);
        var encKey = RandomKey();

        await contactSvc.AddContactAsync(
            new ContactUserData { Username = "Alice", Email = "a@t.com" },
            alice.X25519PublicKey, alice.Ed25519PublicKey,
            SyncRole.Owner, TrustLevel.Full, TrustDirection.Sent, encKey);

        var map = await permSvc.GetPermissionMapAsync();
        Assert.Single(map);
        Assert.Empty(map[alice.Ed25519PublicKey]); // empty = full access
    }

    [Fact]
    public async Task PermissionService_GuestReadonly()
    {
        var (alice, _, tom) = await CreateKeysAsync();
        var contactSvc = new ContactService(_context, _crypto);
        var permSvc = new PermissionService(_context);
        var encKey = RandomKey();

        await contactSvc.AddContactAsync(
            new ContactUserData { Username = "Alice", Email = "a@t.com" },
            alice.X25519PublicKey, alice.Ed25519PublicKey,
            SyncRole.Owner, TrustLevel.Full, TrustDirection.Sent, encKey);
        await contactSvc.AddContactAsync(
            new ContactUserData { Username = "Tom", Email = "t@t.com" },
            tom.X25519PublicKey, tom.Ed25519PublicKey,
            SyncRole.Viewer, TrustLevel.Full, TrustDirection.Received, encKey);

        // Add guest permission
        _context.Permissions.Add(new SyncPermission
        {
            Id = Guid.NewGuid(),
            Role = SyncRole.Viewer,
            TableName = "ShoppingItems",
            PermissionDiffJson = "{\"ShoppingItems\":\"readonly\",\"ShoppingItems.IsBought\":\"readwrite\"}",
            UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var map = await permSvc.GetPermissionMapAsync();
        Assert.Equal(2, map.Count);
        Assert.Empty(map[alice.Ed25519PublicKey]); // admin = full
        Assert.Equal("readonly", map[tom.Ed25519PublicKey]["ShoppingItems"]);
        Assert.Equal("readwrite", map[tom.Ed25519PublicKey]["ShoppingItems.IsBought"]);
    }

    [Fact]
    public async Task PermissionService_BuildReadonlyColumnMap()
    {
        var (_, _, tom) = await CreateKeysAsync();
        var contactSvc = new ContactService(_context, _crypto);
        var permSvc = new PermissionService(_context);
        var encKey = RandomKey();

        await contactSvc.AddContactAsync(
            new ContactUserData { Username = "Tom", Email = "t@t.com" },
            tom.X25519PublicKey, tom.Ed25519PublicKey,
            SyncRole.Viewer, TrustLevel.Full, TrustDirection.Received, encKey);

        _context.Permissions.Add(new SyncPermission
        {
            Id = Guid.NewGuid(),
            Role = SyncRole.Viewer,
            TableName = "ShoppingItems",
            PermissionDiffJson = "{\"ShoppingItems\":\"readonly\",\"ShoppingItems.IsBought\":\"readwrite\"}",
            UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var allColumns = new Dictionary<string, string[]>
        {
            ["ShoppingItems"] = ["Name", "Quantity", "Price", "IsBought"]
        };

        var readonlyMap = await permSvc.BuildReadonlyColumnMapAsync(tom.Ed25519PublicKey, allColumns);
        Assert.NotNull(readonlyMap);
        Assert.Contains("ShoppingItems", readonlyMap.Keys);
        Assert.Contains("Name", readonlyMap["ShoppingItems"]);
        Assert.Contains("Price", readonlyMap["ShoppingItems"]);
        Assert.DoesNotContain("IsBought", readonlyMap["ShoppingItems"]);
    }
}
