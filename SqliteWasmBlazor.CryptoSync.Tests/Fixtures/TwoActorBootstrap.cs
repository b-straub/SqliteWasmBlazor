using BlazorPRF.Crypto.Abstractions;
using BlazorPRF.Crypto.Abstractions.Services;
using BlazorPRF.Crypto.Testing;
using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

/// <summary>
/// Two-actor scenario fixture: an admin and one regular user, both bootstrapped
/// to the canonical "ready to sync" state.
///
/// <para>
/// Admin's DB is seeded via HasData (AdminSeed.g.cs) — the admin contact,
/// system ShareGroup, and self-ShareTarget are already present when the
/// context is created. This fixture adds the user contact + user's ShareTarget.
/// </para>
/// </summary>
public sealed class TwoActorBootstrap : IAsyncDisposable
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
        var adminTarget = await admin.Context.ShareTargets
            .SingleAsync(t => t.ShareGroupId == systemGroup.Id
                && t.MemberPublicKey == adminContact.X25519PublicKey);

        // Add user as a trusted contact on admin's side
        var userContactOnAdmin = await admin.Contacts.AddContactAsync(
            new ContactUserData
            {
                Username = userName,
                Email = $"{userName.ToLowerInvariant()}@test.com"
            },
            user.Keys.X25519PublicKey,
            user.Keys.Ed25519PublicKey);

        await admin.Contacts.TrustAsync(userContactOnAdmin.Id);
        await admin.Context.Entry(userContactOnAdmin).ReloadAsync();

        // Issue a ShareTarget for user on the system scope
        var adminPrivKey = Convert.FromBase64String(admin.Keys.X25519PrivateKey);
        var adminWrappedCek = CryptoSyncBootstrap.DeserializeWrappedCek(adminTarget.WrappedContentKey);
        ShareTarget userTargetOnAdmin;
        try
        {
            var addResult = await groupEncryption.AddGroupMembersAsync(
                adminPrivKey,
                adminContact.X25519PublicKey,
                adminWrappedCek,
                [user.Keys.X25519PublicKey],
                systemGroup.GroupContext);

            if (!addResult.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to wrap CEK for user: {addResult.ErrorCode}");
            }

            var userWrappedKey = addResult.Value![0];

            userTargetOnAdmin = new ShareTarget
            {
                Id = Guid.NewGuid(),
                ShareGroupId = systemGroup.Id,
                KeyVersion = systemGroup.KeyVersion,
                MemberPublicKey = user.Keys.X25519PublicKey,
                WrappedContentKey = CryptoSyncBootstrap.SerializeWrappedCek(userWrappedKey.WrappedContentKey),
                Role = SyncRole.Viewer,
                GrantedByContactId = adminContact.Id,
                UpdatedAt = DateTime.UtcNow,
                SharingScope = SharingScope.Public,
                SharingId = CryptoSyncBootstrap.SystemSharingId
            };
            admin.Context.ShareTargets.Add(userTargetOnAdmin);
            await admin.Context.SaveChangesAsync();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(adminPrivKey);
        }

        // Seed user's DB with rows that would arrive on first sync
        // Note: user's DB also has the HasData seed, but with admin's test keypair.
        // For the two-actor test, user needs admin's ACTUAL contact row (matching admin actor's keys).
        // We need to remove the HasData admin and add the correct one.
        var hasDataAdmin = await user.Context.Contacts.SingleOrDefaultAsync(c => c.IsAdmin);
        if (hasDataAdmin is not null)
        {
            user.Context.Contacts.Remove(hasDataAdmin);
        }
        var hasDataDevice = await user.Context.DeviceSettings.SingleOrDefaultAsync();
        if (hasDataDevice is not null)
        {
            user.Context.DeviceSettings.Remove(hasDataDevice);
        }
        var hasDataGroup = await user.Context.ShareGroups.SingleOrDefaultAsync();
        if (hasDataGroup is not null)
        {
            var hasDataTarget = await user.Context.ShareTargets.SingleOrDefaultAsync();
            if (hasDataTarget is not null)
            {
                user.Context.ShareTargets.Remove(hasDataTarget);
            }
            user.Context.ShareGroups.Remove(hasDataGroup);
        }
        await user.Context.SaveChangesAsync();

        // Now seed with the actual test data
        user.Context.DeviceSettings.Add(new DeviceSettings
        {
            Id = Guid.NewGuid(),
            ClientGuid = Guid.NewGuid().ToString(),
            DeviceName = $"{userName} Device",
            IsAdmin = false,
            AdminContactId = adminContact.Id
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
            IsTrusted = adminContact.IsTrusted,
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
            IsTrusted = userContactOnAdmin.IsTrusted,
            UpdatedAt = userContactOnAdmin.UpdatedAt,
            SharingScope = userContactOnAdmin.SharingScope,
            SharingId = userContactOnAdmin.SharingId
        });

        user.Context.ShareGroups.Add(new ShareGroup
        {
            Id = systemGroup.Id,
            GroupContext = systemGroup.GroupContext,
            KeyVersion = systemGroup.KeyVersion,
            AdminPublicKey = systemGroup.AdminPublicKey,
            CreatedAt = systemGroup.CreatedAt,
            UpdatedAt = systemGroup.UpdatedAt,
            SharingScope = systemGroup.SharingScope,
            SharingId = systemGroup.SharingId
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

        await user.Context.SaveChangesAsync();

        return new TwoActorBootstrap(admin, user, crypto, groupEncryption);
    }

    public async ValueTask DisposeAsync()
    {
        await Admin.DisposeAsync();
        await User.DisposeAsync();
    }
}
