using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Unit tests for <see cref="SharingService.UnshareAsync"/> — exercises the
/// FK-walk soft-delete cascade. <c>ShareAsync</c> was removed under the
/// immutable-<c>SharingId</c> rule; cross-group moves go through a future
/// <c>TransferService</c>, not in-place SharingId mutation.
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
    public async Task UnshareAsync_SoftDeletesParentAndAllChildren()
    {
        var listId = await SeedListWithChildrenAsync(itemCount: 3, noteCount: 2);

        var affected = await _sharing.UnshareAsync("TestLists", listId);

        // 1 parent + 3 items + 2 notes = 6 rows soft-deleted
        Assert.Equal(6, affected);

        var list = await _context.TestLists
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(l => l.Id == listId);
        Assert.True(list.IsDeleted);
        Assert.NotNull(list.DeletedAt);

        var items = await _context.TestListItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(i => i.ListId == listId)
            .ToListAsync();
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.True(i.IsDeleted));

        var notes = await _context.TestListNotes
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(n => n.ListId == listId)
            .ToListAsync();
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.True(n.IsDeleted));
    }

    [Fact]
    public async Task UnshareAsync_OnlyAffectsTargetSubtree()
    {
        var listA = await SeedListWithChildrenAsync(itemCount: 2, noteCount: 0);
        var listB = await SeedListWithChildrenAsync(itemCount: 3, noteCount: 0);

        var affected = await _sharing.UnshareAsync("TestLists", listA);
        Assert.Equal(3, affected); // listA + 2 items

        // listB and its items remain undeleted.
        var lB = await _context.TestLists.AsNoTracking().SingleAsync(l => l.Id == listB);
        Assert.False(lB.IsDeleted);
        var itemsB = await _context.TestListItems
            .AsNoTracking()
            .Where(i => i.ListId == listB)
            .ToListAsync();
        Assert.Equal(3, itemsB.Count);
        Assert.All(itemsB, i => Assert.False(i.IsDeleted));
    }

    [Fact]
    public async Task UnshareAsync_NonSyncableTable_Throws()
    {
        await Assert.ThrowsAsync<SqliteException>(() =>
            _sharing.UnshareAsync("TableThatDoesNotExist", Guid.NewGuid()));
    }

    [Fact]
    public async Task UnshareAsync_AlreadyDeletedRow_NoOp()
    {
        var listId = await SeedListWithChildrenAsync(itemCount: 1, noteCount: 0);
        var firstAffected = await _sharing.UnshareAsync("TestLists", listId);
        Assert.Equal(2, firstAffected);

        // Second call is a no-op: both parent and child are already
        // soft-deleted, and the UPDATE filters by IsDeleted = 0.
        var secondAffected = await _sharing.UnshareAsync("TestLists", listId);
        Assert.Equal(0, secondAffected);
    }

    private async Task<Guid> SeedListWithChildrenAsync(int itemCount, int noteCount)
    {
        var listId = Guid.NewGuid();
        var ts = DateTime.UtcNow;

        _context.TestLists.Add(new TestList
        {
            Id = listId,
            Name = $"List-{listId:N}",
            SharingScope = SharingScope.PUBLIC,
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
                SharingScope = SharingScope.PUBLIC,
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
                SharingScope = SharingScope.PUBLIC,
                SharingId = CryptoSyncBootstrap.SystemSharingId,
                UpdatedAt = ts
            });
        }

        await _context.SaveChangesAsync();
        return listId;
    }
}
