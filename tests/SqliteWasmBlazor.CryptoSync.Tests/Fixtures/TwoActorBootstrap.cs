using SqliteWasmBlazor.Crypto.Abstractions;
using SqliteWasmBlazor.Crypto.Abstractions.Services;
using SqliteWasmBlazor.Crypto.BouncyCastle;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

/// <summary>
/// Two-actor scenario fixture: an admin and one regular user, both bootstrapped
/// to the canonical "ready to sync" state via the admin-initiated invitation
/// flow (<see cref="ContactInvitationService.CreateInvitationAsync"/> →
/// <see cref="ContactInvitationService.RespondToInvitationAsync"/> →
/// <see cref="ContactInvitationService.IngestInvitationResponsesAsync"/> →
/// <see cref="ContactInvitationService.PromoteInvitationAsync"/>).
///
/// <para>
/// Admin's DB is seeded via HasData (AdminSeed.g.cs) — the admin contact,
/// system ShareGroup, and self-ShareTarget are already present when the
/// context is created. This fixture also delivers the post-promotion rows
/// to the user's DB so subsequent integration scenarios start from a
/// fully-populated state on both sides.
/// </para>
/// </summary>
internal sealed class TwoActorBootstrap : IAsyncDisposable
{
    public TestActor Admin { get; }
    public TestActor User { get; }
    public ICryptoProvider Crypto { get; }
    public IGroupEncryption GroupEncryption { get; }

    private TwoActorBootstrap(TestActor admin, TestActor user, ICryptoProvider crypto, IGroupEncryption groupEncryption)
    {
        Admin = admin;
        User = user;
        Crypto = crypto;
        GroupEncryption = groupEncryption;
    }

    public static async Task<TwoActorBootstrap> CreateAsync(
        string adminName = "Admin",
        string userName = "Alice")
    {
        var crypto = new BouncyCastleCryptoProvider();
        var groupEncryption = new GroupEncryptionService(crypto);

        var admin = await TestActor.CreateAsync(adminName, isAdmin: true, seedByte: 1, crypto);
        var user = await TestActor.CreateAsync(userName, isAdmin: false, seedByte: 100, crypto);

        // Admin's seed (contact + ShareGroup + ShareTarget + DeviceSettings) is already
        // in the DB via HasData from AdminSeed.g.cs. Read the existing rows.
        var adminContact = await admin.Context.Contacts.SingleAsync(c => c.IsAdmin);
        var systemGroup = await admin.Context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);

        // Drive the admin-initiated invitation flow end-to-end.
        var relay = new InMemorySyncRelay();
        var adminTransport = new InMemorySyncTransport(relay);
        var contactTransport = new InMemorySyncTransport(relay);

        var bundle = await admin.Invitations.CreateInvitationAsync(
            admin.Keys, InvitationTestSalt.Default, userName, $"{userName.ToLowerInvariant()}@test.com");

        await user.Invitations.RespondToInvitationAsync(
            bundle,
            user.Keys,
            new ContactUserData
            {
                Username = userName,
                Email = $"{userName.ToLowerInvariant()}@test.com"
            },
            contactTransport);

        var ingested = await admin.Invitations.IngestInvitationResponsesAsync(
            admin.Keys, adminTransport);
        if (ingested != 1)
        {
            throw new InvalidOperationException(
                $"TwoActorBootstrap: expected 1 ingested invitation, got {ingested}.");
        }

        var userContactOnAdmin = await admin.Invitations.PromoteInvitationAsync(
            bundle.GroupId, admin.Keys, InvitationTestSalt.Default, systemRole: SyncRole.VIEWER);

        var userTargetOnAdmin = await admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == user.Keys.X25519PublicKey);
        var userSelfGroup = await admin.Context.ShareGroups
            .SingleAsync(g => g.GroupAdminPublicKey == user.Keys.X25519PublicKey);
        var userSelfTargetOnAdmin = await admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == userSelfGroup.Id);

        // Seed user's DB with rows that would arrive on first sync. The
        // user's DB starts with HasData admin/system-group/self-group seeds
        // generated with the AdminSeed test keypair — we need to swap those
        // for rows that use the user actor's real keypair. Use raw SQL DELETE
        // because the interceptor's Deleted → soft-delete conversion would
        // otherwise leave the seed rows in the table (just tombstoned), and
        // the subsequent inserts would collide on primary key.
        await user.Context.Database.ExecuteSqlRawAsync("DELETE FROM ShareTargets");
        await user.Context.Database.ExecuteSqlRawAsync("DELETE FROM ShareGroups");
        await user.Context.Database.ExecuteSqlRawAsync("DELETE FROM Contacts");
        await user.Context.Database.ExecuteSqlRawAsync("DELETE FROM DeviceSettings");
        // Detach any tracked seed entities so subsequent Add() calls don't
        // hit identity-conflict in the change tracker.
        user.Context.ChangeTracker.Clear();

        // Now seed with the actual test data
        user.Context.DeviceSettings.Add(new DeviceSettings
        {
            Id = Guid.NewGuid(),
            ClientGuid = Guid.NewGuid().ToString(),
            DeviceName = $"{userName} Device",
            IsAdmin = false,
            AdminContactId = adminContact.Id,
            OwnContactId = userContactOnAdmin.Id
        });

        user.Context.Contacts.Add(new TrustedContact
        {
            Id = adminContact.Id,
            Username = adminContact.Username,
            Email = adminContact.Email,
            Comment = adminContact.Comment,
            X25519PublicKey = adminContact.X25519PublicKey,
            Ed25519PublicKey = adminContact.Ed25519PublicKey,
            IsAdmin = adminContact.IsAdmin,
            UpdatedAt = adminContact.UpdatedAt,
            SharingScope = adminContact.SharingScope,
            SharingId = adminContact.SharingId
        });

        user.Context.Contacts.Add(new TrustedContact
        {
            Id = userContactOnAdmin.Id,
            Username = userContactOnAdmin.Username,
            Email = userContactOnAdmin.Email,
            Comment = userContactOnAdmin.Comment,
            X25519PublicKey = userContactOnAdmin.X25519PublicKey,
            Ed25519PublicKey = userContactOnAdmin.Ed25519PublicKey,
            IsAdmin = userContactOnAdmin.IsAdmin,
            UpdatedAt = userContactOnAdmin.UpdatedAt,
            SharingScope = userContactOnAdmin.SharingScope,
            SharingId = userContactOnAdmin.SharingId
        });

        user.Context.ShareGroups.Add(new ShareGroup
        {
            Id = systemGroup.Id,
            GroupContext = systemGroup.GroupContext,
            KeyVersion = systemGroup.KeyVersion,
            GroupAdminPublicKey = systemGroup.GroupAdminPublicKey,
            CreatedAt = systemGroup.CreatedAt,
            UpdatedAt = systemGroup.UpdatedAt,
            SharingScope = systemGroup.SharingScope,
            SharingId = systemGroup.SharingId
        });

        // Also mirror the user's own self-group rows to user-side so the
        // interceptor can route Client-scoped writes via OwnContactId.
        user.Context.ShareGroups.Add(new ShareGroup
        {
            Id = userSelfGroup.Id,
            GroupContext = userSelfGroup.GroupContext,
            KeyVersion = userSelfGroup.KeyVersion,
            GroupAdminPublicKey = userSelfGroup.GroupAdminPublicKey,
            CreatedAt = userSelfGroup.CreatedAt,
            UpdatedAt = userSelfGroup.UpdatedAt,
            SharingScope = userSelfGroup.SharingScope,
            SharingId = userSelfGroup.SharingId
        });

        user.Context.ShareTargets.Add(new ShareTarget
        {
            Id = userTargetOnAdmin.Id,
            ShareGroupId = userTargetOnAdmin.ShareGroupId,
            KeyVersion = userTargetOnAdmin.KeyVersion,
            MemberPublicKey = userTargetOnAdmin.MemberPublicKey,
            WrappedContentKey = userTargetOnAdmin.WrappedContentKey,
            Role = userTargetOnAdmin.Role,
            GrantedByContactId = userTargetOnAdmin.GrantedByContactId,
            UpdatedAt = userTargetOnAdmin.UpdatedAt,
            SharingScope = userTargetOnAdmin.SharingScope,
            SharingId = userTargetOnAdmin.SharingId
        });

        user.Context.ShareTargets.Add(new ShareTarget
        {
            Id = userSelfTargetOnAdmin.Id,
            ShareGroupId = userSelfTargetOnAdmin.ShareGroupId,
            KeyVersion = userSelfTargetOnAdmin.KeyVersion,
            MemberPublicKey = userSelfTargetOnAdmin.MemberPublicKey,
            WrappedContentKey = userSelfTargetOnAdmin.WrappedContentKey,
            Role = userSelfTargetOnAdmin.Role,
            GrantedByContactId = userSelfTargetOnAdmin.GrantedByContactId,
            UpdatedAt = userSelfTargetOnAdmin.UpdatedAt,
            SharingScope = userSelfTargetOnAdmin.SharingScope,
            SharingId = userSelfTargetOnAdmin.SharingId
        });

        await user.Context.SaveChangesAsync();

        return new TwoActorBootstrap(admin, user, crypto, groupEncryption);
    }

    public async ValueTask DisposeAsync()
    {
        await Admin.DisposeAsync();
        await User.DisposeAsync();
    }
}
