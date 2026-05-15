using MessagePack;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for the wire-format types <see cref="DeltaEnvelope"/>,
/// <see cref="ShadowRowGroup"/>, and <see cref="ShadowRow"/>. Pure data:
/// the goal is just to lock the MessagePack round-trip and the field
/// shape so future changes don't silently break the wire compatibility.
/// </summary>
public class DeltaEnvelopeTests
{
    private static ShadowRow MakeRow(int seed)
    {
        return new ShadowRow
        {
            Id = new Guid($"00000000-0000-0000-0000-{seed:D12}"),
            SharingScope = seed % 3,
            SharingId = seed % 2 == 0 ? "system" : $"scope-{seed}",
            EncryptedRow = [(byte)(seed & 0xff), (byte)((seed >> 8) & 0xff), 0xAA, 0xBB],
            Nonce = Enumerable.Range(0, 12).Select(i => (byte)(seed + i)).ToArray()
        };
    }

    [Fact]
    public void ShadowRow_RoundTripsViaMessagePack()
    {
        var row = MakeRow(42);

        var bytes = MessagePackSerializer.Serialize(row);
        var deserialized = MessagePackSerializer.Deserialize<ShadowRow>(bytes);

        Assert.Equal(row.Id, deserialized.Id);
        Assert.Equal(row.SharingScope, deserialized.SharingScope);
        Assert.Equal(row.SharingId, deserialized.SharingId);
        Assert.Equal(row.EncryptedRow, deserialized.EncryptedRow);
        Assert.Equal(row.Nonce, deserialized.Nonce);
    }

    [Fact]
    public void ShadowRowGroup_RoundTripsViaMessagePack()
    {
        var group = new ShadowRowGroup
        {
            TableName = "Contacts",
            IsSystemTable = true,
            Rows = [MakeRow(1), MakeRow(2), MakeRow(3)]
        };

        var bytes = MessagePackSerializer.Serialize(group);
        var deserialized = MessagePackSerializer.Deserialize<ShadowRowGroup>(bytes);

        Assert.Equal(group.TableName, deserialized.TableName);
        Assert.Equal(group.IsSystemTable, deserialized.IsSystemTable);
        Assert.Equal(group.Rows.Count, deserialized.Rows.Count);
        for (var i = 0; i < group.Rows.Count; i++)
        {
            Assert.Equal(group.Rows[i].Id, deserialized.Rows[i].Id);
            Assert.Equal(group.Rows[i].EncryptedRow, deserialized.Rows[i].EncryptedRow);
        }
    }

    [Fact]
    public void DeltaEnvelope_RoundTripsViaMessagePack()
    {
        var envelope = new DeltaEnvelope
        {
            Version = 1,
            SenderEd25519PublicKey = "BASE64==SENDER==",
            SenderSignature = new byte[64],
            Groups =
            [
                new ShadowRowGroup
                {
                    TableName = "Contacts",
                    IsSystemTable = true,
                    Rows = [MakeRow(10), MakeRow(11)]
                },
                new ShadowRowGroup
                {
                    TableName = "CryptoTestItems",
                    IsSystemTable = false,
                    Rows = [MakeRow(20), MakeRow(21), MakeRow(22)]
                }
            ]
        };

        var bytes = MessagePackSerializer.Serialize(envelope);
        var deserialized = MessagePackSerializer.Deserialize<DeltaEnvelope>(bytes);

        Assert.Equal(envelope.Version, deserialized.Version);
        Assert.Equal(envelope.SenderEd25519PublicKey, deserialized.SenderEd25519PublicKey);
        Assert.Equal(envelope.Groups.Count, deserialized.Groups.Count);
        Assert.Equal("Contacts", deserialized.Groups[0].TableName);
        Assert.True(deserialized.Groups[0].IsSystemTable);
        Assert.Equal(2, deserialized.Groups[0].Rows.Count);
        Assert.Equal("CryptoTestItems", deserialized.Groups[1].TableName);
        Assert.False(deserialized.Groups[1].IsSystemTable);
        Assert.Equal(3, deserialized.Groups[1].Rows.Count);
    }

    [Fact]
    public void DeltaEnvelope_DefaultVersion_IsOne()
    {
        var envelope = new DeltaEnvelope();
        Assert.Equal(1, envelope.Version);
    }

    [Fact]
    public void ShadowRow_EmptyByteFields_RoundTripCleanly()
    {
        // Belt-and-braces: a ShadowRow with empty EncryptedRow / Nonce
        // should serialize and deserialize without nulls.
        var row = new ShadowRow
        {
            Id = Guid.NewGuid(),
            SharingScope = 0,
            SharingId = "x",
            EncryptedRow = [],
            Nonce = []
        };

        var bytes = MessagePackSerializer.Serialize(row);
        var deserialized = MessagePackSerializer.Deserialize<ShadowRow>(bytes);

        Assert.NotNull(deserialized.EncryptedRow);
        Assert.NotNull(deserialized.Nonce);
        Assert.Empty(deserialized.EncryptedRow);
        Assert.Empty(deserialized.Nonce);
    }

    [Fact]
    public void DeltaEnvelope_GroupsCanBeFilteredBySystemFlag()
    {
        // The receiver's staged-apply routing depends on this filter.
        var envelope = new DeltaEnvelope
        {
            Groups =
            [
                new ShadowRowGroup { TableName = "A", IsSystemTable = true },
                new ShadowRowGroup { TableName = "B", IsSystemTable = false },
                new ShadowRowGroup { TableName = "C", IsSystemTable = true },
                new ShadowRowGroup { TableName = "D", IsSystemTable = false }
            ]
        };

        var systemGroups = envelope.Groups.Where(g => g.IsSystemTable).ToList();
        var domainGroups = envelope.Groups.Where(g => !g.IsSystemTable).ToList();

        Assert.Equal(2, systemGroups.Count);
        Assert.Equal(2, domainGroups.Count);
        Assert.Equal(["A", "C"], systemGroups.Select(g => g.TableName));
        Assert.Equal(["B", "D"], domainGroups.Select(g => g.TableName));
    }
}
