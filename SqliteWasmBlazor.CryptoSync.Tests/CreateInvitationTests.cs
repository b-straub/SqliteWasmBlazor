using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Stage 4b — admin-initiated invitation creation. Covers
/// <see cref="ContactService.CreateInvitationAsync"/>: placeholder row
/// shape, token randomness + length, bundle contents, multi-invitation
/// uniqueness (no UNIQUE-conflict on null pubkeys).
/// </summary>
public class CreateInvitationTests : IAsyncLifetime
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

    [Fact]
    public async Task CreateInvitation_PersistsPlaceholderRow_WithInvitedStatus()
    {
        var bundle = await _scenario.Admin.Contacts.CreateInvitationAsync(
            username: "Bob",
            email: "bob@test.com",
            comment: "Bob from accounting");

        var placeholder = await _scenario.Admin.Context.Contacts
            .SingleAsync(c => c.Username == "Bob");

        Assert.Equal(ContactStatus.Invited, placeholder.Status);
        Assert.False(placeholder.IsAdmin);
        Assert.Null(placeholder.X25519PublicKey);
        Assert.Null(placeholder.Ed25519PublicKey);
        Assert.Equal("bob@test.com", placeholder.Email);
        Assert.Equal("Bob from accounting", placeholder.Comment);
        Assert.NotNull(placeholder.InvitedAt);
        Assert.Null(placeholder.VerifiedAt);
        Assert.Null(placeholder.TrustedAt);
        Assert.Equal(SharingScope.PUBLIC, placeholder.SharingScope);
        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, placeholder.SharingId);

        Assert.NotNull(bundle);
    }

    [Fact]
    public async Task CreateInvitation_GeneratesThirtyTwoByteToken()
    {
        var bundle = await _scenario.Admin.Contacts.CreateInvitationAsync(username: "Bob");

        Assert.Equal(ContactService.InvitationTokenSize, bundle.Token.Length);
        Assert.Equal(32, bundle.Token.Length);

        var placeholder = await _scenario.Admin.Context.Contacts
            .SingleAsync(c => c.Username == "Bob");
        Assert.NotNull(placeholder.InvitationToken);
        Assert.Equal(bundle.Token, placeholder.InvitationToken);
    }

    [Fact]
    public async Task CreateInvitation_TokenLooksRandom_AcrossInvitations()
    {
        // Two invitations, two different tokens. Sanity check on the RNG
        // (not a statistical randomness test — just a "didn't return zeros twice" guard).
        var b1 = await _scenario.Admin.Contacts.CreateInvitationAsync(username: "Carol");
        var b2 = await _scenario.Admin.Contacts.CreateInvitationAsync(username: "Dave");

        Assert.NotEqual(b1.Token, b2.Token);
        Assert.DoesNotContain(b1.Token, b => b == 0 && b1.Token.All(x => x == 0));
        Assert.DoesNotContain(b2.Token, b => b == 0 && b2.Token.All(x => x == 0));
    }

    [Fact]
    public async Task CreateInvitation_BundleCarriesAdminPubKey()
    {
        var bundle = await _scenario.Admin.Contacts.CreateInvitationAsync(username: "Bob");

        Assert.Equal(_scenario.Admin.Keys.X25519PublicKey, bundle.AdminX25519PublicKey);
    }

    [Fact]
    public async Task CreateInvitation_PassesRelayHintIntoBundle()
    {
        var bundle = await _scenario.Admin.Contacts.CreateInvitationAsync(
            username: "Bob",
            relayHint: "https://relay.example/inbox");

        Assert.Equal("https://relay.example/inbox", bundle.RelayHint);
    }

    [Fact]
    public async Task CreateInvitation_OmittingOptionalFields_LeavesNulls()
    {
        var bundle = await _scenario.Admin.Contacts.CreateInvitationAsync(username: "Bob");

        Assert.Null(bundle.RelayHint);
        var placeholder = await _scenario.Admin.Context.Contacts
            .SingleAsync(c => c.Username == "Bob");
        Assert.Null(placeholder.Email);
        Assert.Null(placeholder.Comment);
    }

    [Fact]
    public async Task CreateInvitation_TwoConcurrentPlaceholders_NoUniqueConflict()
    {
        // Both placeholders have null pubkeys — SQLite's UNIQUE on
        // X25519PublicKey/Ed25519PublicKey must accept multiple nulls.
        await _scenario.Admin.Contacts.CreateInvitationAsync(username: "Carol");
        await _scenario.Admin.Contacts.CreateInvitationAsync(username: "Dave");

        var pending = await _scenario.Admin.Context.Contacts
            .Where(c => c.Status == ContactStatus.Invited)
            .CountAsync();
        Assert.Equal(2, pending);
    }

    [Fact]
    public async Task CreateInvitation_RejectsBlankUsername()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _scenario.Admin.Contacts.CreateInvitationAsync(username: "  ").AsTask());
    }
}
