using MessagePack;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for <see cref="V2CryptoHeader"/> — the per-call worker metadata
/// record. Locks the shape (both the record surface and the MessagePack
/// serialization) and the constructor invariants so Stages 5/6 can assume
/// well-formed input.
/// </summary>
public class V2CryptoHeaderTests
{
    private static byte[] MakePrivateKey(byte seed)
    {
        var key = new byte[32];
        for (var i = 0; i < 32; i++)
        {
            key[i] = (byte)(seed + i);
        }
        return key;
    }

    [Fact]
    public void Create_WithAllInputs_PopulatesAllFields()
    {
        var contactId = Guid.NewGuid();
        var privateKey = MakePrivateKey(1);
        var systemTables = new[] { "Contacts", "Permissions" };

        var header = V2CryptoHeader.Create(systemTables, contactId, privateKey);

        Assert.Equal(1, header.Version);
        Assert.Equal(systemTables, header.SystemTables);
        Assert.Equal(V2CryptoHeader.DefaultSharingTableName, header.SharingTableName);
        Assert.Equal(contactId, header.ClientContactId);
        Assert.Equal(privateKey, header.ClientPrivateKey);
    }

    [Fact]
    public void Create_CopiesPrivateKey_DoesNotAliasCallerBuffer()
    {
        var original = MakePrivateKey(7);
        var contactId = Guid.NewGuid();

        var header = V2CryptoHeader.Create(["Contacts"], contactId, original);

        // Mutating the caller's buffer must NOT affect the header copy.
        original[0] = 0xFF;
        Assert.NotEqual(0xFF, header.ClientPrivateKey[0]);
    }

    [Fact]
    public void Create_RejectsWrongSizedPrivateKey()
    {
        var tooShort = new byte[16];
        var tooLong = new byte[64];
        var contactId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() =>
            V2CryptoHeader.Create(["Contacts"], contactId, tooShort));
        Assert.Throws<ArgumentException>(() =>
            V2CryptoHeader.Create(["Contacts"], contactId, tooLong));
    }

    [Fact]
    public void Create_CustomSharingTableName_IsHonored()
    {
        var header = V2CryptoHeader.Create(
            ["Contacts"],
            Guid.NewGuid(),
            MakePrivateKey(2),
            sharingTableName: "CustomSharingKeys");

        Assert.Equal("CustomSharingKeys", header.SharingTableName);
    }

    [Fact]
    public void IsSystemTable_MatchesExactNamesOnly()
    {
        var header = V2CryptoHeader.Create(
            ["Contacts", "Permissions"],
            Guid.NewGuid(),
            MakePrivateKey(3));

        Assert.True(header.IsSystemTable("Contacts"));
        Assert.True(header.IsSystemTable("Permissions"));
        Assert.False(header.IsSystemTable("contacts")); // case-sensitive
        Assert.False(header.IsSystemTable("CryptoTestItems"));
        Assert.False(header.IsSystemTable(""));
    }

    [Fact]
    public void Clear_ZerosPrivateKeyBuffer()
    {
        var header = V2CryptoHeader.Create(
            ["Contacts"],
            Guid.NewGuid(),
            MakePrivateKey(4));

        Assert.Contains(header.ClientPrivateKey, b => b != 0);

        header.Clear();

        Assert.All(header.ClientPrivateKey, b => Assert.Equal(0, b));
    }

    [Fact]
    public void RoundTripsViaMessagePack()
    {
        var contactId = Guid.NewGuid();
        var privateKey = MakePrivateKey(5);
        var header = V2CryptoHeader.Create(
            ["Contacts", "Permissions"],
            contactId,
            privateKey,
            sharingTableName: "SharingKeys");

        var bytes = MessagePackSerializer.Serialize(header);
        var restored = MessagePackSerializer.Deserialize<V2CryptoHeader>(bytes);

        Assert.Equal(header.Version, restored.Version);
        Assert.Equal(header.SystemTables, restored.SystemTables);
        Assert.Equal(header.SharingTableName, restored.SharingTableName);
        Assert.Equal(header.ClientContactId, restored.ClientContactId);
        Assert.Equal(header.ClientPrivateKey, restored.ClientPrivateKey);
    }

    [Fact]
    public void DefaultInstance_HasEmptyButNonNullCollections()
    {
        var header = new V2CryptoHeader();

        Assert.NotNull(header.SystemTables);
        Assert.Empty(header.SystemTables);
        Assert.Equal(V2CryptoHeader.DefaultSharingTableName, header.SharingTableName);
        Assert.NotNull(header.ClientPrivateKey);
        Assert.Empty(header.ClientPrivateKey);
    }
}
