using SqliteWasmBlazor.Crypto.BouncyCastle;
using Xunit;
using Xunit.Abstractions;

namespace SqliteWasmBlazor.CryptoSync.Tests;

// Cross-language byte-equality vectors for the dual-keypair derivation
// silently depended on by group encryption and cross-device sync.
//
// The C# path is BouncyCastleCryptoProvider.DeriveDualKeyPairAsync, which
// runs HKDF-SHA256(seed, salt=zeros[32], info=X25519/Ed25519_INFO, 32) to
// produce the X25519 / Ed25519 private keys, then derives the public keys
// via the BouncyCastle X25519/Ed25519 parameter classes.
//
// The TS path is `deriveDualKeyPair` in
// `src/Base/SqliteWasmBlazor/TypeScript-Crypto/packages/crypto-core/src/keyDerivation.ts`,
// which runs the same HKDF over @awasm/noble + @noble/curves and derives
// the public keys via `x25519.getPublicKey` / `ed25519.getPublicKey`.
//
// The vectors below are the source of truth. Mirrored in the vitest
// suite at `crypto-core/tests/crossLanguageVectors.test.ts`. If either
// side regresses, both sides must be updated together.
public class CrossLanguageKdfVectorTests
{
    private readonly BouncyCastleCryptoProvider _crypto = new();
    private readonly ITestOutputHelper _output;

    public CrossLanguageKdfVectorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Skip = "vector capture; un-skip locally to regenerate golden bytes if HKDF/X25519/Ed25519 inputs change")]
    public async Task PrintGoldenVectors()
    {
        foreach (var entry in Vectors)
        {
            var label = (string)entry[0];
            var seed = (byte[])entry[1];
            var dual = await _crypto.DeriveDualKeyPairAsync(seed);
            _output.WriteLine($"{label}:");
            _output.WriteLine($"  x25519Priv = {dual.X25519PrivateKey}");
            _output.WriteLine($"  x25519Pub  = {dual.X25519PublicKey}");
            _output.WriteLine($"  ed25519Priv = {dual.Ed25519PrivateKey}");
            _output.WriteLine($"  ed25519Pub  = {dual.Ed25519PublicKey}");
        }
    }

    public static IEnumerable<object[]> Vectors => new[]
    {
        // Seed 1 — all zero bytes.
        new object[]
        {
            "zeros",
            new byte[32],
            "7123Lmo81uTvaGg6mF/JHqPhpk/g2YtPRXG5PtQiJuA=",
            "uB1Tmve6CWhkpqTqviODol9a+swrW48ctr+q2FEOrk8=",
            "kfzKlKy3yQgFjDEHipNjhjIHzCmFbexUPp72od049pc=",
            "lbQ0JurxU0mL7EibMqPsGb5R6VpAjJcyScMgUnN6nG0=",
        },

        // Seed 2 — all 0xff bytes.
        new object[]
        {
            "ones",
            Enumerable.Repeat((byte)0xff, 32).ToArray(),
            "yNQ0da8rxEFYER3M903RWvMDPw2y8NIOBmkhqkP/ROE=",
            "u45/9JR3LlzecTORCQs3u3xt1eigRpVkojP/wj/zniI=",
            "DhU9qjHh9pcTSEMh+UVKhENp1SwE0B+rOvZ6B64xG0w=",
            "MCm92C4YIPnc8sV0y9Xya8LT8s582DbPk/EddoyRVOU=",
        },

        // Seed 3 — sequential 0x00 .. 0x1f (a typical PRF-shaped pattern).
        new object[]
        {
            "sequential",
            Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
            "ri9ZbRNMb0SzjNxfBZBXQBaHZLbng3wL2LlbDeIQYl0=",
            "TNo8xxNR1wr2kEcDUpHPPGFY0ejFcFnMz38vVepD/XA=",
            "iuFbXySpLbndD+opZJtoZDwH3vAnQlcnHhsfvucOLjE=",
            "A70zgmy0TJvCGW54qLHbV+LQJu//kdFSTaZ5xIYB7aw=",
        },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task DeriveDualKeyPair_MatchesGoldenVector(
        string label,
        byte[] seed,
        string expectedX25519Priv,
        string expectedX25519Pub,
        string expectedEd25519Priv,
        string expectedEd25519Pub)
    {
        Assert.Equal(32, seed.Length);

        var dual = await _crypto.DeriveDualKeyPairAsync(seed);

        Assert.Equal(expectedX25519Priv, dual.X25519PrivateKey);
        Assert.Equal(expectedX25519Pub, dual.X25519PublicKey);
        Assert.Equal(expectedEd25519Priv, dual.Ed25519PrivateKey);
        Assert.Equal(expectedEd25519Pub, dual.Ed25519PublicKey);

        _ = label;
    }

    [Fact]
    public async Task DeriveDualKeyPair_IsDeterministic()
    {
        var seed = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();

        var first = await _crypto.DeriveDualKeyPairAsync(seed);
        var second = await _crypto.DeriveDualKeyPairAsync(seed);

        Assert.Equal(first.X25519PrivateKey, second.X25519PrivateKey);
        Assert.Equal(first.X25519PublicKey, second.X25519PublicKey);
        Assert.Equal(first.Ed25519PrivateKey, second.Ed25519PrivateKey);
        Assert.Equal(first.Ed25519PublicKey, second.Ed25519PublicKey);
    }
}
