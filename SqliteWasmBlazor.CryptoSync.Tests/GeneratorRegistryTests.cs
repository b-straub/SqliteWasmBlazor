using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Validates the source generator's output against the test project's
/// <see cref="TestSyncContext"/>. The generator emits crypto shadow classes,
/// registries, and column metadata for all syncable entities.
///
/// After the compile-time-only refactor: SyncPermission and ColumnRegistryEntry
/// are local-only (no [SystemTable], no SyncableEntity inheritance) — they do NOT
/// get shadow tables or registry entries. Only domain entities (SyncableEntity)
/// and mutable system tables ([SystemTable]: TrustedContact, ShareGroup, ShareTarget)
/// participate in sync.
/// </summary>
public class GeneratorRegistryTests
{
    [Fact]
    public void CryptoTableRegistry_IncludesDomainEntity()
    {
        Assert.Contains(CryptoTableRegistry.Tables,
            t => t.EntityName == "TestItem"
                 && t.CryptoTableName == "_crypto_TestItems"
                 && t.OpenTableName == "TestItems");
    }

    [Fact]
    public void CryptoTableRegistry_IncludesSystemTrustedContact()
    {
        Assert.Contains(CryptoTableRegistry.Tables,
            t => t.EntityName == "TrustedContact"
                 && t.CryptoTableName == "_crypto_Contacts"
                 && t.OpenTableName == "Contacts");
    }

    [Fact]
    public void CryptoTableRegistry_ExcludesLocalOnlyTables()
    {
        // SyncPermission and ColumnRegistryEntry are compile-time immutable,
        // local-only — they must NOT have shadow tables.
        Assert.DoesNotContain(CryptoTableRegistry.Tables, t => t.EntityName == "SyncPermission");
        Assert.DoesNotContain(CryptoTableRegistry.Tables, t => t.EntityName == "ColumnRegistryEntry");
    }

    [Fact]
    public void CryptoTableRegistry_DoesNotIncludeStandaloneNonSyncableEntities()
    {
        Assert.DoesNotContain(CryptoTableRegistry.Tables, t => t.EntityName == "SentInvitation");
        Assert.DoesNotContain(CryptoTableRegistry.Tables, t => t.EntityName == "ReceivedInvitation");
        Assert.DoesNotContain(CryptoTableRegistry.Tables, t => t.EntityName == "DeviceSettings");
        Assert.DoesNotContain(CryptoTableRegistry.Tables, t => t.EntityName == "SyncState");
    }

    [Fact]
    public void SystemTableRegistry_IncludesMutableSystemEntities()
    {
        Assert.Contains(SystemTableRegistry.Tables, t => t.EntityName == "TrustedContact" && t.TableName == "Contacts");
        Assert.Contains(SystemTableRegistry.Tables, t => t.EntityName == "ShareGroup" && t.TableName == "ShareGroups");
        Assert.Contains(SystemTableRegistry.Tables, t => t.EntityName == "ShareTarget" && t.TableName == "ShareTargets");
    }

    [Fact]
    public void SystemTableRegistry_ExcludesLocalOnlyTables()
    {
        Assert.False(SystemTableRegistry.IsSystem("Permissions"));
        Assert.False(SystemTableRegistry.IsSystem("ColumnRegistry"));
    }

    [Fact]
    public void SystemTableRegistry_IsSystem_True_ForKnownSystemTables()
    {
        Assert.True(SystemTableRegistry.IsSystem("Contacts"));
        Assert.True(SystemTableRegistry.IsSystem("ShareGroups"));
        Assert.True(SystemTableRegistry.IsSystem("ShareTargets"));
    }

    [Fact]
    public void SystemTableRegistry_IsSystem_False_ForDomainTable()
    {
        Assert.False(SystemTableRegistry.IsSystem("TestItems"));
    }

    [Fact]
    public void Crypto_TestItem_ShadowClass_IsGenerated()
    {
        var shadow = new Crypto_TestItem
        {
            Id = Guid.NewGuid(),
            SharingScope = 0,
            SharingId = "test",
            EncryptedRow = [1, 2, 3],
            Nonce = [4, 5, 6]
        };
        Assert.Equal("test", shadow.SharingId);
    }

    [Fact]
    public void Crypto_TrustedContact_ShadowClass_IsGenerated()
    {
        var shadow = new Crypto_TrustedContact
        {
            Id = Guid.NewGuid(),
            SharingScope = 0,
            SharingId = "system",
            EncryptedRow = [1, 2, 3],
            Nonce = [4, 5, 6]
        };
        Assert.Equal("system", shadow.SharingId);
    }
}
