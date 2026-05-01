using SqliteWasmBlazor.Crypto.BouncyCastle;
using Xunit;
using Xunit.Abstractions;

namespace SqliteWasmBlazor.CryptoSync.Tests;

// Cross-language byte-equality vectors for AES-256-GCM at the primitive level —
// the engine underneath `WrapContentKey` / `UnwrapContentKey` and ad-hoc
// symmetric encryption flows.
//
// C# path: `CryptoOperations.EncryptAesGcm(plaintext, key, nonce, aad?)` and
// `DecryptAesGcm(...)` via BouncyCastle GcmBlockCipher (GCM tag length = 128
// bits, appended to ciphertext).
//
// TS path: `crypto.subtle.encrypt({name: 'AES-GCM', iv, additionalData?}, key,
// plaintext)` via SubtleCrypto (RFC 5116 AEAD, GCM tag length = 128 bits,
// appended to ciphertext).
//
// Production wrappers — `wrapContentKey` (TS) and `WrapContentKeyAsync` (C#) —
// generate a random nonce internally so they cannot be vector-tested directly.
// This file pins the underlying AES-GCM primitive: a fixed (key, nonce, plaintext,
// aad?) tuple must produce the same ciphertext+tag bytes on both sides. The
// wrappers' wire format (`{ciphertext, nonce}`) is correct iff this primitive
// agrees.
//
// Mirrored at `crypto-core/tests/crossLanguageAesGcm.test.ts`.
public class CrossLanguageAesGcmVectorTests
{
    private readonly ITestOutputHelper _output;

    public CrossLanguageAesGcmVectorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly byte[] Key32Zeros = new byte[32];
    private static readonly byte[] Key32Ones = Enumerable.Repeat((byte)0xff, 32).ToArray();
    private static readonly byte[] Key32Sequential = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static readonly byte[] NonceCounting = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
    private static readonly byte[] NonceMaxed = Enumerable.Repeat((byte)0xab, 12).ToArray();

    private static readonly byte[] PlaintextHello = "Hello, World!\0\0\0"u8.ToArray(); // 16 bytes
    private static readonly byte[] PlaintextEmpty = Array.Empty<byte>();
    private static readonly byte[] PlaintextLong = Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray();

    [Fact(Skip = "vector capture; un-skip locally to regenerate golden bytes if AES-GCM inputs change")]
    public void PrintGoldenVectors()
    {
        foreach (var entry in Vectors)
        {
            var label = (string)entry[0];
            var key = (byte[])entry[1];
            var nonce = (byte[])entry[2];
            var plaintext = (byte[])entry[3];
            var aad = entry[4] as byte[];

            var ciphertext = CryptoOperations.EncryptAesGcm(plaintext, key, nonce, aad);

            _output.WriteLine($"{label}:");
            _output.WriteLine($"  key       = {Convert.ToBase64String(key)}");
            _output.WriteLine($"  nonce     = {Convert.ToBase64String(nonce)}");
            _output.WriteLine($"  plaintext = {Convert.ToBase64String(plaintext)} ({plaintext.Length} bytes)");
            _output.WriteLine($"  aad       = {(aad is null ? "(none)" : Convert.ToBase64String(aad))}");
            _output.WriteLine($"  ctWithTag = {Convert.ToBase64String(ciphertext)} ({ciphertext.Length} bytes)");
        }
    }

    public static IEnumerable<object[]> Vectors => new[]
    {
        // 1. Zeros key, counting nonce, "Hello, World!" plaintext, no AAD.
        new object[]
        {
            "zeros-key, counting-nonce, hello-plain",
            Key32Zeros,
            NonceCounting,
            PlaintextHello,
            null!,
            "wK5fPm/Rr66tCj6B3APeDNjS1n1eJyqwmVIV94GO/v0=",
        },

        // 2. Ones key, maxed nonce, empty plaintext (tag-only output), no AAD.
        new object[]
        {
            "ones-key, maxed-nonce, empty-plain",
            Key32Ones,
            NonceMaxed,
            PlaintextEmpty,
            null!,
            "sGQSwNi8mT7Ke7mwUpuoNQ==",
        },

        // 3. Sequential key, counting nonce, long plaintext, AAD = "header:v1".
        new object[]
        {
            "sequential-key, counting-nonce, long-plain, with-aad",
            Key32Sequential,
            NonceCounting,
            PlaintextLong,
            "header:v1"u8.ToArray(),
            "RwHQEsnq0A6VWomqlc5SQLPlsQ3MRB05cCyr1Ek+Wu9hc8iVw65g7QzfAWwMAKK1fsr2FMZJAX+XPISorFRPU1p31QVCyT+5013TBQHhuCU=",
        },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void EncryptAesGcm_MatchesGoldenCiphertext(
        string label,
        byte[] key,
        byte[] nonce,
        byte[] plaintext,
        byte[]? aad,
        string expectedCiphertextBase64)
    {
        var actual = CryptoOperations.EncryptAesGcm(plaintext, key, nonce, aad);
        Assert.Equal(expectedCiphertextBase64, Convert.ToBase64String(actual));
        _ = label;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void DecryptAesGcm_RoundTripsGoldenCiphertext(
        string label,
        byte[] key,
        byte[] nonce,
        byte[] plaintext,
        byte[]? aad,
        string expectedCiphertextBase64)
    {
        var ciphertext = Convert.FromBase64String(expectedCiphertextBase64);

        var roundTripped = CryptoOperations.DecryptAesGcm(ciphertext, key, nonce, aad);

        Assert.NotNull(roundTripped);
        Assert.Equal(plaintext, roundTripped);
        _ = label;
    }

    [Fact]
    public void DecryptAesGcm_ReturnsNullOnTagMismatch()
    {
        var ciphertext = Convert.FromBase64String("WUWWBT3HLOdsBKxBMrLxA0EJYy/JDl1XfdCNwDcXXAA=");
        ciphertext[^1] ^= 0x01; // flip a bit in the auth tag

        var result = CryptoOperations.DecryptAesGcm(ciphertext, Key32Zeros, NonceCounting);

        Assert.Null(result);
    }

    [Fact]
    public void EncryptAesGcm_AadAffectsTag()
    {
        var without = CryptoOperations.EncryptAesGcm(PlaintextHello, Key32Zeros, NonceCounting);
        var withAad = CryptoOperations.EncryptAesGcm(PlaintextHello, Key32Zeros, NonceCounting, "header:v1"u8.ToArray());

        Assert.NotEqual(Convert.ToBase64String(without), Convert.ToBase64String(withAad));
    }
}
