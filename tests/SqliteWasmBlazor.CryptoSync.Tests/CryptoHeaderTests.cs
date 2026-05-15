using MessagePack;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

public class CryptoHeaderTests
{
    private static byte[] MakeKey(byte seed, int len = 32)
    {
        var key = new byte[len];
        for (var i = 0; i < len; i++)
        {
            key[i] = (byte)(seed + i);
        }
        return key;
    }

    private static CryptoHeader MakeHeader(byte seed = 1)
    {
        return new CryptoHeader
        {
            Version = 2,
            SystemTables = ["Contacts", "Permissions"],
            ClientContactId = Guid.NewGuid(),
            ClientX25519PrivateKey = MakeKey(seed),
            AdminX25519PublicKey = MakeKey((byte)(seed + 50)),
            GroupContext = "system:v1",
            KeyVersion = 1,
            WrappedCek = MakeKey((byte)(seed + 100), 44), // nonce(12) + ciphertext(32)
            ClientEd25519PrivateKey = MakeKey((byte)(seed + 150)),
            ClientEd25519PublicKey = MakeKey((byte)(seed + 200))
        };
    }

    [Fact]
    public void DefaultInstance_HasSafeDefaults()
    {
        var header = new CryptoHeader();
        Assert.Equal(2, header.Version);
        Assert.NotNull(header.SystemTables);
        Assert.Empty(header.SystemTables);
        Assert.NotNull(header.ClientX25519PrivateKey);
        Assert.Empty(header.ClientX25519PrivateKey);
        Assert.NotNull(header.WrappedCek);
        Assert.Empty(header.WrappedCek);
    }

    [Fact]
    public void IsSystemTable_MatchesExactNames()
    {
        var header = MakeHeader();
        Assert.True(header.IsSystemTable("Contacts"));
        Assert.True(header.IsSystemTable("Permissions"));
        Assert.False(header.IsSystemTable("contacts"));
        Assert.False(header.IsSystemTable("CryptoTestItems"));
    }

    [Fact]
    public void Clear_ZerosPrivateKeyBuffers()
    {
        var header = MakeHeader();
        Assert.Contains(header.ClientX25519PrivateKey, b => b != 0);
        Assert.Contains(header.ClientEd25519PrivateKey, b => b != 0);

        header.Clear();

        Assert.All(header.ClientX25519PrivateKey, b => Assert.Equal(0, b));
        Assert.All(header.ClientEd25519PrivateKey, b => Assert.Equal(0, b));
    }

    [Fact]
    public void RoundTripsViaMessagePack()
    {
        var header = MakeHeader();

        var bytes = MessagePackSerializer.Serialize(header);
        var restored = MessagePackSerializer.Deserialize<CryptoHeader>(bytes);

        Assert.Equal(header.Version, restored.Version);
        Assert.Equal(header.SystemTables, restored.SystemTables);
        Assert.Equal(header.ClientContactId, restored.ClientContactId);
        Assert.Equal(header.ClientX25519PrivateKey, restored.ClientX25519PrivateKey);
        Assert.Equal(header.AdminX25519PublicKey, restored.AdminX25519PublicKey);
        Assert.Equal(header.GroupContext, restored.GroupContext);
        Assert.Equal(header.KeyVersion, restored.KeyVersion);
        Assert.Equal(header.WrappedCek, restored.WrappedCek);
        Assert.Equal(header.ClientEd25519PrivateKey, restored.ClientEd25519PrivateKey);
        Assert.Equal(header.ClientEd25519PublicKey, restored.ClientEd25519PublicKey);
    }

    [Fact]
    public void GroupContext_AndKeyVersion_FormAadString()
    {
        var header = MakeHeader();
        var aad = $"{header.GroupContext}:{header.KeyVersion}";
        Assert.Equal("system:v1:1", aad);
    }
}
