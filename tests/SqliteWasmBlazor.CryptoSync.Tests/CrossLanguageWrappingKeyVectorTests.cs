using SqliteWasmBlazor.Crypto.BouncyCastle;
using Xunit;
using Xunit.Abstractions;

namespace SqliteWasmBlazor.CryptoSync.Tests;

// Cross-language byte-equality vectors for `DeriveWrappingKey`
// (X25519 ECDH + HKDF-SHA256 with caller-supplied context).
//
// C# path: BouncyCastleCryptoProvider.DeriveWrappingKeyAsync, which runs
// X25519 ECDH(ownPriv, recipientPub) and HKDF-SHA256(sharedSecret,
// salt=zeros[32], info=UTF-8(context), 32) via BouncyCastle.
//
// TS path: `deriveWrappingKey` in
// `src/Base/SqliteWasmBlazor/TypeScript-Crypto/src/crypto-core/keyDerivation.ts`,
// which runs the same ECDH via @noble/curves and HKDF via @awasm/noble.
//
// Vectors are mirrored at
// `crypto-core/tests/crossLanguageWrappingKey.test.ts`. Both sides MUST
// agree on every byte; the wrapping key is what binds CEKs across
// devices in the multi-recipient sharing primitive.
public class CrossLanguageWrappingKeyVectorTests
{
    private readonly BouncyCastleCryptoProvider _crypto = new();
    private readonly ITestOutputHelper _output;

    public CrossLanguageWrappingKeyVectorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // PRF seeds (32 bytes each) — same shape as CrossLanguageKdfVectorTests so
    // we reuse the deterministic dual-keypair output to produce X25519 priv/pub
    // pairs without inventing fresh derivations.
    private static readonly byte[] OwnerSeed = new byte[32];                                               // zeros
    private static readonly byte[] RecipientSeed = Enumerable.Repeat((byte)0xff, 32).ToArray();            // ones
    private static readonly byte[] AltSeed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();       // sequential

    [Fact(Skip = "vector capture; un-skip locally to regenerate golden bytes if HKDF/X25519/info inputs change")]
    public async Task PrintGoldenVectors()
    {
        foreach (var entry in Vectors)
        {
            var label = (string)entry[0];
            var ownerSeed = (byte[])entry[1];
            var recipientSeed = (byte[])entry[2];
            var context = (string)entry[3];

            var owner = await _crypto.DeriveDualKeyPairAsync(ownerSeed);
            var recipient = await _crypto.DeriveDualKeyPairAsync(recipientSeed);

            var ownPriv = Convert.FromBase64String(owner.X25519PrivateKey);
            var wrapping = await _crypto.DeriveWrappingKeyAsync(ownPriv, recipient.X25519PublicKey, context);

            Assert.True(wrapping.Success);
            _output.WriteLine($"{label}:");
            _output.WriteLine($"  ownX25519Priv      = {owner.X25519PrivateKey}");
            _output.WriteLine($"  recipientX25519Pub = {recipient.X25519PublicKey}");
            _output.WriteLine($"  context            = \"{context}\"");
            _output.WriteLine($"  wrappingKey        = {Convert.ToBase64String(wrapping.Value.ToArray())}");
        }
    }

    public static IEnumerable<object[]> Vectors => new[]
    {
        // Owner=zeros, Recipient=ones, group context.
        new object[]
        {
            "zeros->ones, group:v1",
            OwnerSeed,
            RecipientSeed,
            "group:v1",
            "UghC2EP75/swpP5nt316lfSPZVJh5O9hfNyOPNfD8bQ=",
        },

        // Owner=ones, Recipient=sequential, ECIES info string.
        new object[]
        {
            "ones->sequential, ecies-aes-gcm",
            RecipientSeed,
            AltSeed,
            "ecies-aes-gcm",
            "8dIqYwsHM2yASQujK/cyxUEbip53FzT9sjs0x/vow3k=",
        },

        // Owner=sequential, Recipient=zeros, empty context (edge case).
        new object[]
        {
            "sequential->zeros, empty context",
            AltSeed,
            OwnerSeed,
            string.Empty,
            "+ZpodXMedmT/qFovITlJIGExWDE7zZo+xiJO0OJkhyU=",
        },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task DeriveWrappingKey_MatchesGoldenVector(
        string label,
        byte[] ownerSeed,
        byte[] recipientSeed,
        string context,
        string expectedWrappingKey)
    {
        Assert.Equal(32, ownerSeed.Length);
        Assert.Equal(32, recipientSeed.Length);

        var owner = await _crypto.DeriveDualKeyPairAsync(ownerSeed);
        var recipient = await _crypto.DeriveDualKeyPairAsync(recipientSeed);

        var ownPriv = Convert.FromBase64String(owner.X25519PrivateKey);
        var wrapping = await _crypto.DeriveWrappingKeyAsync(ownPriv, recipient.X25519PublicKey, context);

        Assert.True(wrapping.Success, $"DeriveWrappingKey failed: {wrapping.ErrorCode}");

        var actual = Convert.ToBase64String(wrapping.Value.ToArray());
        Assert.Equal(expectedWrappingKey, actual);

        _ = label;
    }

    [Fact]
    public async Task DeriveWrappingKey_IsDeterministic()
    {
        var owner = await _crypto.DeriveDualKeyPairAsync(OwnerSeed);
        var recipient = await _crypto.DeriveDualKeyPairAsync(RecipientSeed);

        var ownPriv = Convert.FromBase64String(owner.X25519PrivateKey);

        var first = await _crypto.DeriveWrappingKeyAsync(ownPriv, recipient.X25519PublicKey, "group:v1");
        var second = await _crypto.DeriveWrappingKeyAsync(ownPriv, recipient.X25519PublicKey, "group:v1");

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.Value.ToArray(), second.Value.ToArray());
    }

    [Fact]
    public async Task DeriveWrappingKey_ContextChangeYieldsDifferentKey()
    {
        var owner = await _crypto.DeriveDualKeyPairAsync(OwnerSeed);
        var recipient = await _crypto.DeriveDualKeyPairAsync(RecipientSeed);

        var ownPriv = Convert.FromBase64String(owner.X25519PrivateKey);

        var ctxA = await _crypto.DeriveWrappingKeyAsync(ownPriv, recipient.X25519PublicKey, "ctx-a");
        var ctxB = await _crypto.DeriveWrappingKeyAsync(ownPriv, recipient.X25519PublicKey, "ctx-b");

        Assert.True(ctxA.Success);
        Assert.True(ctxB.Success);
        Assert.NotEqual(ctxA.Value.ToArray(), ctxB.Value.ToArray());
    }
}
