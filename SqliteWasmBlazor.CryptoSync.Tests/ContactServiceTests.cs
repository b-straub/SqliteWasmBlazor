using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for <see cref="ContactService"/> — CRUD operations on trusted contacts.
/// Each test gets a fresh two-actor scenario so we start with admin + user already present.
/// </summary>
public class ContactServiceTests : IAsyncLifetime
{
    private TwoActorBootstrap _scenario = null!;

    public async Task InitializeAsync()
    {
        _scenario = await TwoActorBootstrap.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        await _scenario.DisposeAsync();
    }

    // ----------------------------------------------------------------
    // ADD
    // ----------------------------------------------------------------

    [Fact]
    public async Task AddContact_CreatesUntrustedContactInClientScope()
    {
        var contact = await _scenario.Admin.Contacts.AddContactAsync(
            new ContactUserData { Username = "Bob", Email = "bob@test.com" },
            "bob-x25519-pub",
            "bob-ed25519-pub");

        Assert.False(contact.IsTrusted);
        Assert.Equal(SharingScope.Client, contact.SharingScope);
        Assert.Equal(string.Empty, contact.SharingId);
        Assert.NotEqual(Guid.Empty, contact.Id);
    }

    [Fact]
    public async Task AddContact_WithAdminFlag_SetsIsAdmin()
    {
        var contact = await _scenario.Admin.Contacts.AddContactAsync(
            new ContactUserData { Username = "SuperAdmin", Email = "sa@test.com" },
            "sa-x25519",
            "sa-ed25519",
            isAdmin: true);

        Assert.True(contact.IsAdmin);
    }

    [Fact]
    public async Task AddContact_WithTrustedFlag_SetsIsTrusted()
    {
        var contact = await _scenario.Admin.Contacts.AddContactAsync(
            new ContactUserData { Username = "Trusted", Email = "t@test.com" },
            "t-x25519",
            "t-ed25519",
            isTrusted: true);

        Assert.True(contact.IsTrusted);
    }

    [Fact]
    public async Task AddContact_PersistsToDatabase()
    {
        await _scenario.Admin.Contacts.AddContactAsync(
            new ContactUserData { Username = "Charlie", Email = "charlie@test.com" },
            "charlie-x25519",
            "charlie-ed25519");

        var all = await _scenario.Admin.Contacts.GetAllAsync();
        Assert.Equal(3, all.Count); // admin + user + charlie
    }

    // ----------------------------------------------------------------
    // TRUST / UNTRUST
    // ----------------------------------------------------------------

    [Fact]
    public async Task Trust_MovesToPublicSystemScope()
    {
        var contact = await _scenario.Admin.Contacts.AddContactAsync(
            new ContactUserData { Username = "Pending", Email = "p@test.com" },
            "p-x25519",
            "p-ed25519");

        Assert.False(contact.IsTrusted);

        await _scenario.Admin.Contacts.TrustAsync(contact.Id);
        await _scenario.Admin.Context.Entry(contact).ReloadAsync();

        Assert.True(contact.IsTrusted);
        Assert.Equal(SharingScope.Public, contact.SharingScope);
        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, contact.SharingId);
    }

    [Fact]
    public async Task Untrust_RevertsToClientScope()
    {
        // User contact is already trusted from bootstrap
        var userContact = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.User.Keys.Ed25519PublicKey);
        Assert.NotNull(userContact);
        Assert.True(userContact.IsTrusted);

        await _scenario.Admin.Contacts.UntrustAsync(userContact.Id);
        await _scenario.Admin.Context.Entry(userContact).ReloadAsync();

        Assert.False(userContact.IsTrusted);
        Assert.Equal(SharingScope.Client, userContact.SharingScope);
        Assert.Equal(string.Empty, userContact.SharingId);
    }

    [Fact]
    public async Task Trust_NonExistentContact_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _scenario.Admin.Contacts.TrustAsync(Guid.NewGuid()).AsTask());
    }

    [Fact]
    public async Task Untrust_NonExistentContact_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _scenario.Admin.Contacts.UntrustAsync(Guid.NewGuid()).AsTask());
    }

    // ----------------------------------------------------------------
    // LOOKUP
    // ----------------------------------------------------------------

    [Fact]
    public async Task GetByEd25519PublicKey_ReturnsMatchingContact()
    {
        var found = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync(_scenario.User.Keys.Ed25519PublicKey);

        Assert.NotNull(found);
        Assert.Equal(_scenario.User.Keys.Ed25519PublicKey, found.Ed25519PublicKey);
    }

    [Fact]
    public async Task GetByEd25519PublicKey_ReturnsNull_ForUnknownKey()
    {
        var found = await _scenario.Admin.Contacts
            .GetByEd25519PublicKeyAsync("unknown-key");

        Assert.Null(found);
    }

    [Fact]
    public async Task GetRecipientPublicKeys_ReturnsOnlyTrustedX25519Keys()
    {
        // Add untrusted contact
        await _scenario.Admin.Contacts.AddContactAsync(
            new ContactUserData { Username = "Untrusted", Email = "u@test.com" },
            "u-x25519",
            "u-ed25519");

        var keys = await _scenario.Admin.Contacts.GetRecipientPublicKeysAsync();

        // Admin + User are trusted, Untrusted is not
        Assert.Equal(2, keys.Length);
        Assert.Contains(_scenario.Admin.Keys.X25519PublicKey, keys);
        Assert.Contains(_scenario.User.Keys.X25519PublicKey, keys);
        Assert.DoesNotContain("u-x25519", keys);
    }

    // ----------------------------------------------------------------
    // DELETE (soft-delete)
    // ----------------------------------------------------------------

    [Fact]
    public async Task Delete_SoftDeletesContact()
    {
        var contact = await _scenario.Admin.Contacts.AddContactAsync(
            new ContactUserData { Username = "ToDelete", Email = "d@test.com" },
            "d-x25519",
            "d-ed25519");

        await _scenario.Admin.Contacts.DeleteAsync(contact.Id);

        // Soft-deleted: query filter hides it
        var all = await _scenario.Admin.Contacts.GetAllAsync();
        Assert.Equal(2, all.Count); // admin + user, deleted one hidden

        // But it's still in the DB
        var raw = await _scenario.Admin.Context.Contacts
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(c => c.Id == contact.Id);
        Assert.NotNull(raw);
        Assert.True(raw.IsDeleted);
        Assert.NotNull(raw.DeletedAt);
    }

    [Fact]
    public async Task Delete_NonExistentContact_NoOp()
    {
        // Should not throw
        await _scenario.Admin.Contacts.DeleteAsync(Guid.NewGuid());
    }
}
