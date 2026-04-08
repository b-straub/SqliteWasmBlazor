using System.Security.Cryptography;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for the deterministic content-key derivation helpers
/// (<see cref="KeyDerivation"/>). The whole point is determinism: same
/// input must always produce the same output, with no global state and
/// no randomness leaking in.
/// </summary>
public class KeyDerivationTests
{
    private static byte[] FixedAdminPrivateKey()
    {
        // 32 deterministic bytes — fine for unit tests, never used as a real key.
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(i * 7 + 1);
        }
        return key;
    }

    private static byte[] OtherAdminPrivateKey()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(0xFF - i);
        }
        return key;
    }

    [Fact]
    public void DeriveSystemContentKey_Returns32Bytes()
    {
        var derived = KeyDerivation.DeriveSystemContentKey(FixedAdminPrivateKey());
        Assert.Equal(KeyDerivation.ContentKeyBytes, derived.Length);
        Assert.Equal(32, derived.Length);
    }

    [Fact]
    public void DeriveSystemContentKey_IsDeterministic()
    {
        // Two independent calls with the same input must yield byte-identical output.
        var key = FixedAdminPrivateKey();
        var derived1 = KeyDerivation.DeriveSystemContentKey(key);
        var derived2 = KeyDerivation.DeriveSystemContentKey(key);
        Assert.Equal(derived1, derived2);
    }

    [Fact]
    public void DeriveSystemContentKey_DiffersForDifferentAdmins()
    {
        var derivedA = KeyDerivation.DeriveSystemContentKey(FixedAdminPrivateKey());
        var derivedB = KeyDerivation.DeriveSystemContentKey(OtherAdminPrivateKey());
        Assert.NotEqual(derivedA, derivedB);
    }

    [Fact]
    public void DeriveContentKey_IsDeterministic_PerScope()
    {
        var key = FixedAdminPrivateKey();
        var derived1 = KeyDerivation.DeriveContentKey(key, "groceries-main");
        var derived2 = KeyDerivation.DeriveContentKey(key, "groceries-main");
        Assert.Equal(derived1, derived2);
    }

    [Fact]
    public void DeriveContentKey_DifferentScopes_DiffersFromSameOwner()
    {
        var key = FixedAdminPrivateKey();
        var derivedA = KeyDerivation.DeriveContentKey(key, "groceries-main");
        var derivedB = KeyDerivation.DeriveContentKey(key, "groceries-surprise");
        Assert.NotEqual(derivedA, derivedB);
    }

    [Fact]
    public void DeriveContentKey_DifferentOwners_DiffersForSameScope()
    {
        var derivedA = KeyDerivation.DeriveContentKey(FixedAdminPrivateKey(), "groceries-main");
        var derivedB = KeyDerivation.DeriveContentKey(OtherAdminPrivateKey(), "groceries-main");
        Assert.NotEqual(derivedA, derivedB);
    }

    [Fact]
    public void DeriveSystemContentKey_DiffersFromDeriveContentKey_WithSystemSharingId()
    {
        // The system info string and the domain info string differ, so even
        // when given the same scopeId="system" the two helpers must produce
        // different keys. This prevents accidental cross-derivation collisions.
        var key = FixedAdminPrivateKey();
        var systemKey = KeyDerivation.DeriveSystemContentKey(key);
        var domainKeyForSystemId = KeyDerivation.DeriveContentKey(key, KeyDerivation.SystemSharingId);
        Assert.NotEqual(systemKey, domainKeyForSystemId);
    }

    [Fact]
    public void DeriveSystemContentKey_OutputCanBeZeroed()
    {
        // Defensive check: the returned buffer is mutable so callers can
        // zero it after use (and the function does NOT return a shared instance).
        var key = FixedAdminPrivateKey();
        var derived1 = KeyDerivation.DeriveSystemContentKey(key);
        var copy = (byte[])derived1.Clone();
        CryptographicOperations.ZeroMemory(derived1);

        // Re-derivation must still work and give the original output.
        var derived2 = KeyDerivation.DeriveSystemContentKey(key);
        Assert.Equal(copy, derived2);
        Assert.NotEqual(copy, derived1); // first buffer is now zeros
    }
}
