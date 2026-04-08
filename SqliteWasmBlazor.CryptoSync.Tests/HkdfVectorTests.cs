using System.Security.Cryptography;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Byte-level lock-in for the HKDF-SHA256 behavior used by
/// <see cref="KeyDerivation"/>. The worker side derives content keys with
/// <c>crypto.subtle.deriveBits({ name: 'HKDF', hash: 'SHA-256', … })</c>,
/// which is specified to implement the same RFC 5869 algorithm. If .NET's
/// <see cref="HKDF"/> ever regresses against the published vectors, this
/// test fires before any sync traffic silently corrupts. Browser-side
/// compat relies on WebCrypto's spec conformance, which we treat as a
/// given for modern (OPFS + PRF capable) browsers per project scope.
///
/// <para>
/// Vectors are RFC 5869 Test Case 1 verbatim — SHA-256, short inputs,
/// L=42. If you want to add more cases, Test Cases 2 and 3 live in the
/// same RFC section.
/// </para>
/// </summary>
public class HkdfVectorTests
{
    // RFC 5869 §A.1 Test Case 1
    private static readonly byte[] Tc1Ikm =
        Convert.FromHexString("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
    private static readonly byte[] Tc1Salt =
        Convert.FromHexString("000102030405060708090a0b0c");
    private static readonly byte[] Tc1Info =
        Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9");
    private static readonly byte[] Tc1ExpectedOkm =
        Convert.FromHexString(
            "3cb25f25faacd57a90434f64d0362f2a" +
            "2d2d0a90cf1a5a4c5db02d56ecc4c5bf" +
            "34007208d5b887185865");

    [Fact]
    public void HkdfSha256_Rfc5869TestCase1_MatchesExpectedOkm()
    {
        var actual = new byte[42];
        HKDF.DeriveKey(
            hashAlgorithmName: HashAlgorithmName.SHA256,
            ikm: Tc1Ikm,
            output: actual,
            salt: Tc1Salt,
            info: Tc1Info);

        Assert.Equal(Tc1ExpectedOkm, actual);
    }

    [Fact]
    public void HkdfSha256_EmptySalt_IsSubstitutedWithZeroedHashLength()
    {
        // Per RFC 5869 §2.2, if no salt is provided it MUST be treated as a
        // string of HashLen zero octets. .NET handles this for us; the
        // worker-side WebCrypto implementation does the same. This test
        // locks that substitution — if .NET ever changed to, say, null/zero
        // salt meaning "use a default", the two sides would silently
        // disagree on the PRK and every sync delta would fail to decrypt.
        var explicitZeroSalt = new byte[32];
        var output1 = new byte[32];
        var output2 = new byte[32];

        HKDF.DeriveKey(HashAlgorithmName.SHA256, Tc1Ikm, output1,
            salt: ReadOnlySpan<byte>.Empty, info: Tc1Info);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Tc1Ikm, output2,
            salt: explicitZeroSalt, info: Tc1Info);

        Assert.Equal(output1, output2);
    }

    [Fact]
    public void KeyDerivation_SystemKeyAndDomainKey_AreDistinct()
    {
        // Same private key in, different scopes out — this is the whole
        // point of info-based derivation. If this ever produces equal keys
        // the scope-separation property is broken.
        var privateKey = new byte[32];
        RandomNumberGenerator.Fill(privateKey);

        var systemKey = KeyDerivation.DeriveSystemContentKey(privateKey);
        var domainKey = KeyDerivation.DeriveContentKey(privateKey, "groceries-main");

        Assert.Equal(32, systemKey.Length);
        Assert.Equal(32, domainKey.Length);
        Assert.NotEqual(systemKey, domainKey);

        // Same scope twice → deterministic → equal.
        var domainKeyAgain = KeyDerivation.DeriveContentKey(privateKey, "groceries-main");
        Assert.Equal(domainKey, domainKeyAgain);
    }
}
