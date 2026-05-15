using System.Text;
using SqliteWasmBlazor.Crypto.Abstractions.Models;
using SqliteWasmBlazor.Crypto.BouncyCastle;
using Xunit;
using Xunit.Abstractions;

namespace SqliteWasmBlazor.CryptoSync.Tests;

// Cross-language byte-equality vectors for ECIES (X25519 + HKDF + AES-256-GCM).
//
// C# path: `BouncyCastleCryptoProvider.EncryptAsymmetricAsync` /
// `DecryptAsymmetricAsync` — production code generates a random ephemeral
// keypair and a random nonce, so the production methods aren't directly
// vector-testable. The interop invariant is the *combinator*:
//   sharedSecret = X25519(ephPriv, recipientPub)
//   wrappingKey  = HKDF-SHA256(sharedSecret, salt=zeros[32], info="ecies-aes-gcm", 32)
//   ciphertext   = AES-256-GCM(plaintext, wrappingKey, nonce, aad?)
//
// We pin the combinator with fixed (ephPriv, recipientPriv, nonce, plaintext)
// using the public primitives:
//   `DeriveWrappingKeyAsync` (X25519 + HKDF with caller-supplied context)
//   `CryptoOperations.EncryptAesGcm` (BouncyCastle GCM with caller-supplied nonce)
//
// TS path mirrors via `x25519SharedSecret` + `hkdf("ecies-aes-gcm", 32)` +
// `crypto.subtle.encrypt({iv: fixedNonce})`. Both sides MUST produce the same
// ephemeralPublicKey and ciphertext+tag bytes.
//
// Mirrored at `crypto-core/tests/crossLanguageEcies.test.ts`.
public class CrossLanguageEciesVectorTests
{
    private const string EciesContext = "ecies-aes-gcm";
    private readonly BouncyCastleCryptoProvider _crypto = new();
    private readonly ITestOutputHelper _output;

    public CrossLanguageEciesVectorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly byte[] EphemeralSeed = new byte[32];                                          // zeros
    private static readonly byte[] RecipientSeed = Enumerable.Repeat((byte)0xff, 32).ToArray();           // ones
    private static readonly byte[] AltEphemeralSeed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(); // sequential

    private static readonly byte[] NonceCounting = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
    private static readonly byte[] NonceMaxed = Enumerable.Repeat((byte)0xab, 12).ToArray();

    private const string PlaintextHello = "Hello, ECIES!";
    private const string PlaintextLong = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do.";

    [Fact(Skip = "vector capture; un-skip locally to regenerate golden bytes if ECIES inputs change")]
    public async Task PrintGoldenVectors()
    {
        foreach (var entry in Vectors)
        {
            var label = (string)entry[0];
            var ephSeed = (byte[])entry[1];
            var recipientSeed = (byte[])entry[2];
            var nonce = (byte[])entry[3];
            var plaintext = (string)entry[4];

            var (ephPub, ciphertext) = ComposeEcies(ephSeed, recipientSeed, nonce, plaintext);

            _output.WriteLine($"{label}:");
            _output.WriteLine($"  ephPub    = {ephPub}");
            _output.WriteLine($"  nonce     = {Convert.ToBase64String(nonce)}");
            _output.WriteLine($"  plaintext = \"{plaintext}\"");
            _output.WriteLine($"  ctWithTag = {ciphertext}");
        }
    }

    public static IEnumerable<object[]> Vectors => new[]
    {
        new object[]
        {
            "zeros-eph -> ones-recipient, counting-nonce, hello",
            EphemeralSeed,
            RecipientSeed,
            NonceCounting,
            PlaintextHello,
            "uB1Tmve6CWhkpqTqviODol9a+swrW48ctr+q2FEOrk8=", // ephPub
            "WqWSoNNKDJQdErmofy0MW5boYQ4Bg9fCIXgxJJc=",
        },
        new object[]
        {
            "sequential-eph -> ones-recipient, maxed-nonce, long",
            AltEphemeralSeed,
            RecipientSeed,
            NonceMaxed,
            PlaintextLong,
            "TNo8xxNR1wr2kEcDUpHPPGFY0ejFcFnMz38vVepD/XA=", // ephPub
            "T1707zgxhLdiEjFydlgg+ddrM72zm9CEoPJ3uMFVCoshPAd6B4wKZKTRbg3s0X79lWeRz0hS62K3gqAEjur/8yBhuf0AAyLZZ3+na7hHuho=",
        },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task EciesCombinator_MatchesGoldenBytes(
        string label,
        byte[] ephSeed,
        byte[] recipientSeed,
        byte[] nonce,
        string plaintext,
        string expectedEphPubBase64,
        string expectedCiphertextBase64)
    {
        var (ephPub, ciphertext) = await ComposeEciesAsync(ephSeed, recipientSeed, nonce, plaintext);

        Assert.Equal(expectedEphPubBase64, ephPub);
        Assert.Equal(expectedCiphertextBase64, ciphertext);
        _ = label;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task EciesCombinator_ProductionDecryptRoundTrips(
        string label,
        byte[] ephSeed,
        byte[] recipientSeed,
        byte[] nonce,
        string plaintext,
        string expectedEphPubBase64,
        string expectedCiphertextBase64)
    {
        // The production `DecryptAsymmetricToBytesAsync` consumes the same wire
        // shape (EphemeralPublicKey, Ciphertext, Nonce). Confirm round-trip with
        // the golden bytes against the recipient's actual private key.
        var recipient = await _crypto.DeriveDualKeyPairAsync(recipientSeed);
        var recipientPriv = recipient.X25519PrivateKey;

        var encrypted = new AsymmetricEncryptedData(
            EphemeralPublicKey: expectedEphPubBase64,
            Ciphertext: expectedCiphertextBase64,
            Nonce: Convert.ToBase64String(nonce)
        );

        var result = await _crypto.DecryptAsymmetricToBytesAsync(encrypted, recipientPriv);

        Assert.True(result.Success, $"DecryptAsymmetricToBytesAsync failed: {result.ErrorCode}");
        Assert.NotNull(result.Value);
        try
        {
            Assert.Equal(plaintext, System.Text.Encoding.UTF8.GetString(result.Value));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(result.Value);
        }
        _ = label;
        _ = ephSeed;
    }

    private (string EphPubBase64, string CiphertextBase64) ComposeEcies(
        byte[] ephSeed, byte[] recipientSeed, byte[] nonce, string plaintext)
    {
        return ComposeEciesAsync(ephSeed, recipientSeed, nonce, plaintext).GetAwaiter().GetResult();
    }

    private async Task<(string EphPubBase64, string CiphertextBase64)> ComposeEciesAsync(
        byte[] ephSeed, byte[] recipientSeed, byte[] nonce, string plaintext)
    {
        // Derive deterministic X25519 keypairs from PRF seeds (same trick as
        // CrossLanguageWrappingKeyVectorTests). Reuses the locked KDF vectors so
        // the full ECIES path agrees by construction.
        var ephemeral = await _crypto.DeriveDualKeyPairAsync(ephSeed);
        var recipient = await _crypto.DeriveDualKeyPairAsync(recipientSeed);

        var ephPriv = ephemeral.X25519PrivateKey;

        // X25519 ECDH + HKDF-SHA256 with ECIES context = production ECIES key derivation.
        var wrappingKey = await _crypto.DeriveWrappingKeyAsync(
            ephPriv, recipient.X25519PublicKey, EciesContext);
        Assert.True(wrappingKey.Success);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = CryptoOperations.EncryptAesGcm(
            plaintextBytes, wrappingKey.Value.Span, nonce);

        return (ephemeral.X25519PublicKey, Convert.ToBase64String(ciphertext));
    }

    [Fact]
    public async Task EciesCombinator_IsDeterministic()
    {
        var (ephPub1, ct1) = await ComposeEciesAsync(EphemeralSeed, RecipientSeed, NonceCounting, PlaintextHello);
        var (ephPub2, ct2) = await ComposeEciesAsync(EphemeralSeed, RecipientSeed, NonceCounting, PlaintextHello);

        Assert.Equal(ephPub1, ephPub2);
        Assert.Equal(ct1, ct2);
    }

    [Fact]
    public async Task EciesCombinator_DifferentEphemeralChangesEphPubAndCiphertext()
    {
        var (ephPubA, ctA) = await ComposeEciesAsync(EphemeralSeed, RecipientSeed, NonceCounting, PlaintextHello);
        var (ephPubB, ctB) = await ComposeEciesAsync(AltEphemeralSeed, RecipientSeed, NonceCounting, PlaintextHello);

        Assert.NotEqual(ephPubA, ephPubB);
        Assert.NotEqual(ctA, ctB);
    }

}
