using Xunit;
using BlazorPRF.Crypto.Abstractions;
using BlazorPRF.Crypto.Abstractions.Models;
using BlazorPRF.Crypto.Testing;

namespace SqliteWasmBlazor.CryptoSync.Tests;

public class EncryptedDeltaTests
{
    private readonly ICryptoProvider _crypto = new BouncyCastleCryptoProvider();

    private async Task<(DualKeyPairFull Alice, DualKeyPairFull Bob, DualKeyPairFull Tom)> CreateKeysAsync()
    {
        var seed1 = new byte[32]; Random.Shared.NextBytes(seed1);
        var seed2 = new byte[32]; Random.Shared.NextBytes(seed2);
        var seed3 = new byte[32]; Random.Shared.NextBytes(seed3);

        var alice = await _crypto.DeriveDualKeyPairAsync(seed1);
        var bob = await _crypto.DeriveDualKeyPairAsync(seed2);
        var tom = await _crypto.DeriveDualKeyPairAsync(seed3);

        return (alice, bob, tom);
    }

    private static Dictionary<string, Dictionary<string, string>> AliceBobTomPermissions(
        DualKeyPairFull alice, DualKeyPairFull bob, DualKeyPairFull tom) => new()
    {
        [alice.Ed25519PublicKey] = new(), // full access
        [bob.Ed25519PublicKey] = new(),   // full access
        [tom.Ed25519PublicKey] = new()    // readonly on TodoItems, except IsCompleted
        {
            ["TodoItems"] = "readonly",
            ["TodoItems.IsCompleted"] = "readwrite"
        }
    };

    private static readonly byte[] TestV2Bytes = "SWBV2-test-payload-simulating-messagepack-data"u8.ToArray();

    [Fact]
    public async Task EncryptDecrypt_RoundTrip()
    {
        var (alice, bob, _) = await CreateKeysAsync();
        var permissions = new Dictionary<string, Dictionary<string, string>>
        {
            [alice.Ed25519PublicKey] = new(),
            [bob.Ed25519PublicKey] = new()
        };

        var adminPrivateKey = Convert.FromBase64String(alice.Ed25519PrivateKey);
        var delta = await EncryptedDeltaService.EncryptAsync(
            _crypto, TestV2Bytes, alice,
            [alice.X25519PublicKey, bob.X25519PublicKey],
            permissions, adminPrivateKey, alice.Ed25519PublicKey);

        // Alice decrypts
        var alicePrivate = Convert.FromBase64String(alice.X25519PrivateKey);
        var decrypted = await EncryptedDeltaService.DecryptAsync(
            _crypto, delta, alicePrivate, alice.X25519PublicKey);

        Assert.Equal(TestV2Bytes, decrypted);
    }

    [Fact]
    public async Task MultiRecipient_BothCanDecrypt()
    {
        var (alice, bob, _) = await CreateKeysAsync();
        var permissions = new Dictionary<string, Dictionary<string, string>>
        {
            [alice.Ed25519PublicKey] = new(),
            [bob.Ed25519PublicKey] = new()
        };

        var adminPrivateKey = Convert.FromBase64String(alice.Ed25519PrivateKey);
        var delta = await EncryptedDeltaService.EncryptAsync(
            _crypto, TestV2Bytes, alice,
            [alice.X25519PublicKey, bob.X25519PublicKey],
            permissions, adminPrivateKey, alice.Ed25519PublicKey);

        // Bob decrypts
        var bobPrivate = Convert.FromBase64String(bob.X25519PrivateKey);
        var decrypted = await EncryptedDeltaService.DecryptAsync(
            _crypto, delta, bobPrivate, bob.X25519PublicKey);

        Assert.Equal(TestV2Bytes, decrypted);
    }

    [Fact]
    public async Task WrongRecipient_Rejected()
    {
        var (alice, bob, tom) = await CreateKeysAsync();
        var permissions = new Dictionary<string, Dictionary<string, string>>
        {
            [alice.Ed25519PublicKey] = new(),
            [bob.Ed25519PublicKey] = new()
        };

        var adminPrivateKey = Convert.FromBase64String(alice.Ed25519PrivateKey);
        var delta = await EncryptedDeltaService.EncryptAsync(
            _crypto, TestV2Bytes, alice,
            [alice.X25519PublicKey, bob.X25519PublicKey],  // Tom excluded
            permissions, adminPrivateKey, alice.Ed25519PublicKey);

        var tomPrivate = Convert.FromBase64String(tom.X25519PrivateKey);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EncryptedDeltaService.DecryptAsync(_crypto, delta, tomPrivate, tom.X25519PublicKey).AsTask());

        Assert.Contains("not encrypted for this recipient", ex.Message);
    }

    [Fact]
    public async Task TamperedCiphertext_SignatureFails()
    {
        var (alice, bob, _) = await CreateKeysAsync();
        var permissions = new Dictionary<string, Dictionary<string, string>>
        {
            [alice.Ed25519PublicKey] = new(),
            [bob.Ed25519PublicKey] = new()
        };

        var adminPrivateKey = Convert.FromBase64String(alice.Ed25519PrivateKey);
        var delta = await EncryptedDeltaService.EncryptAsync(
            _crypto, TestV2Bytes, alice,
            [alice.X25519PublicKey, bob.X25519PublicKey],
            permissions, adminPrivateKey, alice.Ed25519PublicKey);

        // Tamper ciphertext
        delta.Ciphertext[0] ^= 0xFF;

        var bobPrivate = Convert.FromBase64String(bob.X25519PrivateKey);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EncryptedDeltaService.DecryptAsync(_crypto, delta, bobPrivate, bob.X25519PublicKey).AsTask());

        Assert.Contains("Content signature verification failed", ex.Message);
    }

    [Fact]
    public async Task TamperedPermissions_SignatureFails()
    {
        var (alice, bob, tom) = await CreateKeysAsync();
        var permissions = AliceBobTomPermissions(alice, bob, tom);

        var adminPrivateKey = Convert.FromBase64String(alice.Ed25519PrivateKey);
        var delta = await EncryptedDeltaService.EncryptAsync(
            _crypto, TestV2Bytes, alice,
            [alice.X25519PublicKey, bob.X25519PublicKey, tom.X25519PublicKey],
            permissions, adminPrivateKey, alice.Ed25519PublicKey);

        // Tamper permissions — give Tom full access
        delta.Permissions[tom.Ed25519PublicKey] = new();

        var bobPrivate = Convert.FromBase64String(bob.X25519PrivateKey);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EncryptedDeltaService.DecryptAsync(_crypto, delta, bobPrivate, bob.X25519PublicKey).AsTask());

        Assert.Contains("Permissions signature verification failed", ex.Message);
    }

    [Fact]
    public async Task MessagePack_SerializeDeserialize_RoundTrip()
    {
        var (alice, bob, _) = await CreateKeysAsync();
        var permissions = new Dictionary<string, Dictionary<string, string>>
        {
            [alice.Ed25519PublicKey] = new(),
            [bob.Ed25519PublicKey] = new()
        };

        var adminPrivateKey = Convert.FromBase64String(alice.Ed25519PrivateKey);
        var delta = await EncryptedDeltaService.EncryptAsync(
            _crypto, TestV2Bytes, alice,
            [alice.X25519PublicKey, bob.X25519PublicKey],
            permissions, adminPrivateKey, alice.Ed25519PublicKey);

        var bytes = EncryptedDeltaService.Serialize(delta);
        var restored = EncryptedDeltaService.Deserialize(bytes);

        Assert.Equal(delta.SenderPublicKey, restored.SenderPublicKey);
        Assert.Equal(delta.AdminPublicKey, restored.AdminPublicKey);
        Assert.Equal(delta.Ciphertext, restored.Ciphertext);
        Assert.Equal(2, restored.RecipientEnvelopes.Count);
    }

    // ============================================================
    // PERMISSION TESTS
    // ============================================================

    [Fact]
    public async Task Permissions_FullAccess()
    {
        var (alice, _, _) = await CreateKeysAsync();
        var permissions = new Dictionary<string, Dictionary<string, string>>
        {
            [alice.Ed25519PublicKey] = new()
        };

        var result = PermissionHelper.CheckWriteAccess(permissions, alice.Ed25519PublicKey, "TodoItems", ["Title", "Price"]);
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task Permissions_ReadonlyTable_Rejected()
    {
        var (_, _, tom) = await CreateKeysAsync();
        var permissions = new Dictionary<string, Dictionary<string, string>>
        {
            [tom.Ed25519PublicKey] = new() { ["TodoItems"] = "readonly" }
        };

        var result = PermissionHelper.CheckWriteAccess(permissions, tom.Ed25519PublicKey, "TodoItems", ["Title"]);
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Permissions_ColumnOverride_Allowed()
    {
        var (_, _, tom) = await CreateKeysAsync();
        var permissions = new Dictionary<string, Dictionary<string, string>>
        {
            [tom.Ed25519PublicKey] = new()
            {
                ["TodoItems"] = "readonly",
                ["TodoItems.IsCompleted"] = "readwrite"
            }
        };

        var result = PermissionHelper.CheckWriteAccess(permissions, tom.Ed25519PublicKey, "TodoItems", ["IsCompleted"]);
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task Permissions_UnknownSender_Rejected()
    {
        var (alice, _, _) = await CreateKeysAsync();
        var permissions = new Dictionary<string, Dictionary<string, string>>
        {
            [alice.Ed25519PublicKey] = new()
        };

        var result = PermissionHelper.CheckWriteAccess(permissions, "unknown-pk", "TodoItems", ["Title"]);
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Permissions_GetReadonlyColumns()
    {
        var (_, _, tom) = await CreateKeysAsync();
        var permissions = new Dictionary<string, Dictionary<string, string>>
        {
            [tom.Ed25519PublicKey] = new()
            {
                ["TodoItems"] = "readonly",
                ["TodoItems.IsCompleted"] = "readwrite"
            }
        };

        var readonlyCols = PermissionHelper.GetReadonlyColumns(
            permissions, tom.Ed25519PublicKey, "TodoItems",
            ["Title", "Description", "Price", "IsCompleted"]);

        Assert.Contains("Title", readonlyCols);
        Assert.Contains("Description", readonlyCols);
        Assert.Contains("Price", readonlyCols);
        Assert.DoesNotContain("IsCompleted", readonlyCols);
    }

    [Fact]
    public void PermissionHash_Deterministic()
    {
        var permissions = new Dictionary<string, Dictionary<string, string>>
        {
            ["pk-a"] = new(),
            ["pk-b"] = new() { ["Table"] = "readonly" }
        };

        var hash1 = PermissionHelper.HashPermissions(permissions);
        var hash2 = PermissionHelper.HashPermissions(permissions);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void PermissionHash_DifferentOrder_SameHash()
    {
        var p1 = new Dictionary<string, Dictionary<string, string>>
        {
            ["pk-a"] = new(), ["pk-b"] = new()
        };
        var p2 = new Dictionary<string, Dictionary<string, string>>
        {
            ["pk-b"] = new(), ["pk-a"] = new()
        };

        Assert.Equal(
            PermissionHelper.HashPermissions(p1),
            PermissionHelper.HashPermissions(p2));
    }
}
