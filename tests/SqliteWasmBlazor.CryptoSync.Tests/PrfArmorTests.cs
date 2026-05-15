using SqliteWasmBlazor.Crypto.Abstractions.Formatting;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

public class PrfArmorTests
{
    private const string SamplePublicKey =
        "MCowBQYDK2VuAyEAGb9ECWmEzf6FQbrBZ9w7lshQhqowtrbLDFw4rXAxZ1g=";

    [Fact]
    public void ArmorPublicKey_NoMetadata_RoundTrips()
    {
        var armored = PrfArmor.ArmorPublicKey(SamplePublicKey);
        var (key, metadata) = PrfArmor.UnArmorPublicKeyWithMetadata(armored);

        Assert.Equal(SamplePublicKey, key);
        Assert.Null(metadata);
    }

    [Fact]
    public void ArmorPublicKey_WithCredentialId_RoundTrips()
    {
        var input = new PublicKeyMetadata
        {
            CredentialId = "Y3JlZC1pZC1zYW1wbGU="
        };

        var armored = PrfArmor.ArmorPublicKey(SamplePublicKey, input);
        var (key, metadata) = PrfArmor.UnArmorPublicKeyWithMetadata(armored);

        Assert.Equal(SamplePublicKey, key);
        Assert.NotNull(metadata);
        Assert.Equal(input.CredentialId, metadata!.CredentialId);
        Assert.Null(metadata.Name);
        Assert.Null(metadata.Email);
    }

    [Fact]
    public void ArmorPublicKey_AllMetadataFields_RoundTrips()
    {
        var input = new PublicKeyMetadata
        {
            Name = "Alice",
            Email = "alice@example.com",
            Comment = "demo identity",
            Created = new DateOnly(2026, 5, 13),
            CredentialId = "Y3JlZC1pZC1hbGljZQ=="
        };

        var armored = PrfArmor.ArmorPublicKey(SamplePublicKey, input);
        var (key, metadata) = PrfArmor.UnArmorPublicKeyWithMetadata(armored);

        Assert.Equal(SamplePublicKey, key);
        Assert.NotNull(metadata);
        Assert.Equal(input.Name, metadata!.Name);
        Assert.Equal(input.Email, metadata.Email);
        Assert.Equal(input.Comment, metadata.Comment);
        Assert.Equal(input.Created, metadata.Created);
        Assert.Equal(input.CredentialId, metadata.CredentialId);
    }

    [Fact]
    public void UnArmorPublicKey_MissingCredentialIdField_LeavesItNull()
    {
        // An armored block produced WITHOUT a credentialId field (legacy /
        // older blocks) must parse without throwing and surface CredentialId
        // as null on the returned metadata.
        var legacy = PrfArmor.ArmorPublicKey(SamplePublicKey, new PublicKeyMetadata
        {
            Name = "Legacy"
        });

        var (key, metadata) = PrfArmor.UnArmorPublicKeyWithMetadata(legacy);

        Assert.Equal(SamplePublicKey, key);
        Assert.NotNull(metadata);
        Assert.Equal("Legacy", metadata!.Name);
        Assert.Null(metadata.CredentialId);
    }
}
