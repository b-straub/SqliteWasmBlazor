using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Unit tests for <see cref="SharingService"/> — validates the FK-walk
/// cascade that propagates a sharing assignment from a parent row down
/// through every child entity type that references it.
/// </summary>
public class SharingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestSyncContext _context;
    private readonly SharingService _sharing;

    public SharingServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TestSyncContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new TestSyncContext(options);
        _context.Database.EnsureCreated();

        _sharing = new SharingService(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ShareAsync_WithExistingGroup_CascadesToChildren()
    {
        // Arrange: a list with 3 items and 2 notes, all currently Public/system
        var listId = await SeedListWithChildrenAsync(itemCount: 3, noteCount: 2);
        var group = new ShareGroup
        {
            Id = Guid.NewGuid(),
            GroupContext = "list-abc:v1",
            KeyVersion = 1,
            AdminPublicKey = Convert.ToBase64String(new byte[32]),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.Public,
            SharingId = "list-abc"
        };

        // Act
        var affected = await _sharing.ShareAsync("TestLists", listId, group);

        // Assert: parent + 3 items + 2 notes = 6 rows updated
        Assert.Equal(6, affected);

        // AsNoTracking so we see the post-UPDATE values, not the stale
        // copies still sitting in EF's change tracker from the seed.
        var list = await _context.TestLists.AsNoTracking().SingleAsync(l => l.Id == listId);
        Assert.Equal(SharingScope.Shared, list.SharingScope);
        Assert.Equal("list-abc", list.SharingId);

        var items = await _context.TestListItems.AsNoTracking().Where(i => i.ListId == listId).ToListAsync();
        Assert.All(items, i =>
        {
            Assert.Equal(SharingScope.Shared, i.SharingScope);
            Assert.Equal("list-abc", i.SharingId);
        });

        var notes = await _context.TestListNotes.AsNoTracking().Where(n => n.ListId == listId).ToListAsync();
        Assert.All(notes, n =>
        {
            Assert.Equal(SharingScope.Shared, n.SharingScope);
            Assert.Equal("list-abc", n.SharingId);
        });
    }

    [Fact]
    public async Task ShareAsync_BumpsUpdatedAtOnEveryRow()
    {
        var baseline = DateTime.UtcNow.AddMinutes(-10);
        var listId = await SeedListWithChildrenAsync(itemCount: 2, noteCount: 1, updatedAt: baseline);

        var group = new ShareGroup
        {
            Id = Guid.NewGuid(),
            GroupContext = "x:v1",
            KeyVersion = 1,
            AdminPublicKey = Convert.ToBase64String(new byte[32]),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.Public,
            SharingId = "list-x"
        };

        var before = DateTime.UtcNow;
        await _sharing.ShareAsync("TestLists", listId, group);
        var after = DateTime.UtcNow;

        var list = await _context.TestLists.AsNoTracking().SingleAsync(l => l.Id == listId);
        Assert.InRange(list.UpdatedAt, before.AddSeconds(-1), after.AddSeconds(1));

        var items = await _context.TestListItems.AsNoTracking().Where(i => i.ListId == listId).ToListAsync();
        Assert.All(items, i => Assert.InRange(i.UpdatedAt, before.AddSeconds(-1), after.AddSeconds(1)));
    }

    [Fact]
    public async Task UnshareAsync_RevertsSubtreeToPublicSystem()
    {
        // Arrange: pre-share a list so the subtree is in the Shared state
        var listId = await SeedListWithChildrenAsync(itemCount: 2, noteCount: 1);
        var group = new ShareGroup
        {
            Id = Guid.NewGuid(),
            GroupContext = "y:v1",
            KeyVersion = 1,
            AdminPublicKey = Convert.ToBase64String(new byte[32]),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.Public,
            SharingId = "list-y"
        };
        await _sharing.ShareAsync("TestLists", listId, group);

        // Act: revert
        var affected = await _sharing.UnshareAsync("TestLists", listId);

        // Assert: parent + 2 items + 1 note = 4 rows
        Assert.Equal(4, affected);

        var list = await _context.TestLists.AsNoTracking().SingleAsync(l => l.Id == listId);
        Assert.Equal(SharingScope.Public, list.SharingScope);
        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, list.SharingId);

        var items = await _context.TestListItems.AsNoTracking().Where(i => i.ListId == listId).ToListAsync();
        Assert.All(items, i =>
        {
            Assert.Equal(SharingScope.Public, i.SharingScope);
            Assert.Equal(CryptoSyncBootstrap.SystemSharingId, i.SharingId);
        });
    }

    [Fact]
    public async Task ShareAsync_OnlyAffectsDescendantsOfTargetParent()
    {
        // Arrange: two sibling lists, each with their own items
        var listA = await SeedListWithChildrenAsync(itemCount: 2, noteCount: 0);
        var listB = await SeedListWithChildrenAsync(itemCount: 3, noteCount: 0);

        var group = new ShareGroup
        {
            Id = Guid.NewGuid(),
            GroupContext = "A:v1",
            KeyVersion = 1,
            AdminPublicKey = Convert.ToBase64String(new byte[32]),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.Public,
            SharingId = "list-A"
        };

        // Act: share list A only
        var affected = await _sharing.ShareAsync("TestLists", listA, group);
        Assert.Equal(3, affected); // 1 parent + 2 items

        // Assert: list A subtree shifted, list B subtree untouched
        var lA = await _context.TestLists.AsNoTracking().SingleAsync(l => l.Id == listA);
        Assert.Equal("list-A", lA.SharingId);
        var itemsA = await _context.TestListItems.AsNoTracking().Where(i => i.ListId == listA).ToListAsync();
        Assert.All(itemsA, i => Assert.Equal("list-A", i.SharingId));

        var lB = await _context.TestLists.AsNoTracking().SingleAsync(l => l.Id == listB);
        Assert.Equal(CryptoSyncBootstrap.SystemSharingId, lB.SharingId);
        var itemsB = await _context.TestListItems.AsNoTracking().Where(i => i.ListId == listB).ToListAsync();
        Assert.All(itemsB, i => Assert.Equal(CryptoSyncBootstrap.SystemSharingId, i.SharingId));
    }

    [Fact]
    public async Task ShareAsync_NonSyncableTable_Throws()
    {
        var group = new ShareGroup
        {
            Id = Guid.NewGuid(),
            GroupContext = "x:v1",
            KeyVersion = 1,
            AdminPublicKey = Convert.ToBase64String(new byte[32]),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.Public,
            SharingId = "nope"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sharing.ShareAsync("TableThatDoesNotExist", Guid.NewGuid(), group));
    }

    [Fact]
    public async Task ShareAsync_EmptySharingIdOnGroup_Throws()
    {
        var listId = await SeedListWithChildrenAsync(itemCount: 1, noteCount: 0);
        var bad = new ShareGroup
        {
            Id = Guid.NewGuid(),
            GroupContext = "x:v1",
            KeyVersion = 1,
            AdminPublicKey = Convert.ToBase64String(new byte[32]),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SharingScope = SharingScope.Public,
            SharingId = "" // empty — should fail fast
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sharing.ShareAsync("TestLists", listId, bad));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<Guid> SeedListWithChildrenAsync(int itemCount, int noteCount, DateTime? updatedAt = null)
    {
        var listId = Guid.NewGuid();
        var ts = updatedAt ?? DateTime.UtcNow;

        _context.TestLists.Add(new TestList
        {
            Id = listId,
            Name = $"List-{listId:N}",
            SharingScope = SharingScope.Public,
            SharingId = CryptoSyncBootstrap.SystemSharingId,
            UpdatedAt = ts
        });

        for (var i = 0; i < itemCount; i++)
        {
            _context.TestListItems.Add(new TestListItem
            {
                Id = Guid.NewGuid(),
                ListId = listId,
                ItemName = $"Item-{i}",
                Quantity = i + 1,
                SharingScope = SharingScope.Public,
                SharingId = CryptoSyncBootstrap.SystemSharingId,
                UpdatedAt = ts
            });
        }

        for (var i = 0; i < noteCount; i++)
        {
            _context.TestListNotes.Add(new TestListNote
            {
                Id = Guid.NewGuid(),
                ListId = listId,
                Text = $"Note-{i}",
                SharingScope = SharingScope.Public,
                SharingId = CryptoSyncBootstrap.SystemSharingId,
                UpdatedAt = ts
            });
        }

        await _context.SaveChangesAsync();
        return listId;
    }
}
