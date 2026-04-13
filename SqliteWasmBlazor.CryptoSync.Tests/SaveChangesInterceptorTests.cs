using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Tests for <see cref="CryptoSyncSaveChangesInterceptor"/>: lifecycle
/// metadata auto-population, scope-driven SharingId routing, immutability
/// guard, and FK-cascade soft delete.
/// </summary>
public class SaveChangesInterceptorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestSyncContext _context;

    public SaveChangesInterceptorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<TestSyncContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new TestSyncContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Guid> AdminContactIdAsync()
    {
        var settings = await _context.DeviceSettings.AsNoTracking().SingleAsync();
        return settings.OwnContactId ?? throw new InvalidOperationException("OwnContactId not seeded");
    }

    // ----------------------------------------------------------------
    // Added — defaults + scope routing
    // ----------------------------------------------------------------

    [Fact]
    public async Task Added_EntityWithoutSharingId_DefaultsToClientSelfGroup()
    {
        var ownContactId = await AdminContactIdAsync();
        var list = new TestList { Name = "Private" };
        _context.TestLists.Add(list);
        await _context.SaveChangesAsync();

        Assert.NotEqual(Guid.Empty, list.Id);
        Assert.Equal(SharingScope.CLIENT, list.SharingScope);
        Assert.Equal(CryptoSyncBootstrap.BuildSelfGroupContext(ownContactId), list.SharingId);
        Assert.NotEqual(default, list.UpdatedAt);
    }

    [Fact]
    public async Task Added_PublicScope_GetsSystemSharingId()
    {
        var list = new TestList { Name = "Shared with everyone", SharingScope = SharingScope.PUBLIC };
        _context.TestLists.Add(list);
        await _context.SaveChangesAsync();

        Assert.Equal(SharingScope.PUBLIC, list.SharingScope);
        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, list.SharingId);
    }

    [Fact]
    public async Task Added_SharedScopeWithoutExplicitSharingId_Throws()
    {
        var list = new TestList { Name = "Shared", SharingScope = SharingScope.SHARED };
        _context.TestLists.Add(list);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _context.SaveChangesAsync());
        Assert.Contains("Shared", ex.Message);
        Assert.Contains("explicit SharingId", ex.Message);
    }

    [Fact]
    public async Task Added_CallerSetSharingId_LeftAlone()
    {
        var list = new TestList
        {
            Name = "Custom",
            SharingScope = SharingScope.SHARED,
            SharingId = "custom-group:v1"
        };
        _context.TestLists.Add(list);
        await _context.SaveChangesAsync();

        Assert.Equal("custom-group:v1", list.SharingId);
        Assert.Equal(SharingScope.SHARED, list.SharingScope);
    }

    [Fact]
    public async Task Added_SystemTableShortCircuit_RoutesToSystemSharingId()
    {
        // ShareGroup is [SystemTable]. Even with SharingScope = Client,
        // the interceptor must stamp SharingId = "system" so the row
        // travels via the system CEK envelope.
        var group = new ShareGroup
        {
            Id = Guid.NewGuid(),
            GroupContext = "self-test-system-table:v1",
            KeyVersion = 1,
            GroupAdminPublicKey = Convert.ToBase64String(new byte[32]),
            CreatedAt = DateTime.UtcNow,
            // Deliberately Client + empty SharingId to confirm the
            // interceptor short-circuits both "leave alone" and "scope route".
            SharingScope = SharingScope.CLIENT,
            SharingId = string.Empty
        };
        _context.ShareGroups.Add(group);
        await _context.SaveChangesAsync();

        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, group.SharingId);
    }

    [Fact]
    public async Task Added_ChildWithInheritPermissions_CopiesParentScopeAndId()
    {
        // Seed a parent list explicitly in a "shared" group context.
        var parent = new TestList
        {
            Name = "Inherit-parent",
            SharingScope = SharingScope.SHARED,
            SharingId = "parent-group:v1"
        };
        _context.TestLists.Add(parent);

        // Add the child in the same SaveChanges batch — interceptor finds
        // the tracked parent via FK and copies its scope.
        var child = new TestInheritedItem { ListId = parent.Id, Label = "child-1" };
        _context.TestInheritedItems.Add(child);

        await _context.SaveChangesAsync();

        Assert.Equal(SharingScope.SHARED, child.SharingScope);
        Assert.Equal("parent-group:v1", child.SharingId);
    }

    [Fact]
    public async Task Added_NewGuid_AssignedWhenIdEmpty()
    {
        var list = new TestList { Name = "Auto-id" };
        Assert.Equal(Guid.Empty, list.Id);
        _context.TestLists.Add(list);
        await _context.SaveChangesAsync();
        Assert.NotEqual(Guid.Empty, list.Id);
    }

    [Fact]
    public async Task Added_OwnContactIdMissing_ThrowsClearDiagnostic()
    {
        // Wipe OwnContactId on the seeded device row to simulate a non-admin
        // device that hasn't completed first-sync identity wiring.
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE DeviceSettings SET OwnContactId = NULL");
        _context.ChangeTracker.Clear();

        var list = new TestList { Name = "Should fail" };
        _context.TestLists.Add(list);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _context.SaveChangesAsync());
        Assert.Contains("OwnContactId", ex.Message);
        Assert.Contains("SetOwnContactIdAsync", ex.Message);
    }

    // ----------------------------------------------------------------
    // Modified — UpdatedAt bump + immutability guard
    // ----------------------------------------------------------------

    [Fact]
    public async Task Modified_BumpsUpdatedAt()
    {
        var list = new TestList { Name = "Bumped", SharingScope = SharingScope.PUBLIC };
        _context.TestLists.Add(list);
        await _context.SaveChangesAsync();
        var initialUpdatedAt = list.UpdatedAt;

        await Task.Delay(20);
        list.Name = "Bumped (renamed)";
        await _context.SaveChangesAsync();

        Assert.True(list.UpdatedAt > initialUpdatedAt);
    }

    [Fact]
    public async Task Modified_ChangingSharingId_Throws()
    {
        var list = new TestList { Name = "Locked", SharingScope = SharingScope.PUBLIC };
        _context.TestLists.Add(list);
        await _context.SaveChangesAsync();

        list.SharingId = "different-group:v1";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _context.SaveChangesAsync());
        Assert.Contains("SharingId", ex.Message);
        Assert.Contains("immutable", ex.Message);
    }

    [Fact]
    public async Task Modified_ChangingSharingScope_Throws()
    {
        var list = new TestList { Name = "Scope-locked", SharingScope = SharingScope.PUBLIC };
        _context.TestLists.Add(list);
        await _context.SaveChangesAsync();

        list.SharingScope = SharingScope.CLIENT;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _context.SaveChangesAsync());
        Assert.Contains("SharingScope", ex.Message);
        Assert.Contains("immutable", ex.Message);
    }

    // ----------------------------------------------------------------
    // Deleted — soft-delete + FK cascade
    // ----------------------------------------------------------------

    [Fact]
    public async Task Deleted_ConvertsToTombstone_AndCascadesViaFkWalk()
    {
        var parent = new TestList { Name = "To delete", SharingScope = SharingScope.PUBLIC };
        _context.TestLists.Add(parent);
        await _context.SaveChangesAsync();

        // Add 3 children + 2 notes (children populated separately to avoid
        // navigation tracking weirdness).
        for (var i = 0; i < 3; i++)
        {
            _context.TestListItems.Add(new TestListItem
            {
                Id = Guid.NewGuid(),
                ListId = parent.Id,
                ItemName = $"item-{i}",
                Quantity = 1,
                SharingScope = SharingScope.PUBLIC
            });
        }
        for (var i = 0; i < 2; i++)
        {
            _context.TestListNotes.Add(new TestListNote
            {
                Id = Guid.NewGuid(),
                ListId = parent.Id,
                Text = $"note-{i}",
                SharingScope = SharingScope.PUBLIC
            });
        }
        await _context.SaveChangesAsync();

        // Soft-delete via property mutation. Remove() would trip EF's
        // navigation-severance guard on OnDelete.Restrict relationships —
        // the recommended pattern with this interceptor is to flip
        // IsDeleted directly and let the Modified branch cascade.
        parent.IsDeleted = true;
        await _context.SaveChangesAsync();

        // Parent: still in DB, soft-deleted
        var parentRaw = await _context.TestLists
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(l => l.Id == parent.Id);
        Assert.True(parentRaw.IsDeleted);
        Assert.NotNull(parentRaw.DeletedAt);

        // Children: 3 items + 2 notes also soft-deleted
        var items = await _context.TestListItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(i => i.ListId == parent.Id)
            .ToListAsync();
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.True(i.IsDeleted));

        var notes = await _context.TestListNotes
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(n => n.ListId == parent.Id)
            .ToListAsync();
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.True(n.IsDeleted));
    }
}
