using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

public class TransferServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestSyncContext _context;
    private readonly TransferService _transfer;

    public TransferServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<TestSyncContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new TestSyncContext(options);
        _context.Database.EnsureCreated();
        _transfer = new TransferService(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // ----------------------------------------------------------------
    // Single entity transfer
    // ----------------------------------------------------------------

    [Fact]
    public async Task TransferSubtree_SingleEntity_TombstonesSourceAndCreatesClone()
    {
        var list = new TestList
        {
            Name = "Shopping",
            SharingScope = SharingScope.Shared,
            SharingId = "group-a:v1"
        };
        _context.TestLists.Add(list);
        await _context.SaveChangesAsync();
        var originalId = list.Id;

        var idMap = await _transfer.TransferSubtreeAsync(
            "TestLists", originalId, "group-b:v1", SharingScope.Shared);

        // Source row: tombstoned, original SharingId preserved.
        var source = await _context.TestLists
            .IgnoreQueryFilters()
            .SingleAsync(l => l.Id == originalId);
        Assert.True(source.IsDeleted);
        Assert.Equal("group-a:v1", source.SharingId);

        // Clone: new Id, target group, same domain data.
        var newId = idMap[originalId];
        var clone = await _context.TestLists
            .IgnoreQueryFilters()
            .SingleAsync(l => l.Id == newId);
        Assert.False(clone.IsDeleted);
        Assert.Equal("group-b:v1", clone.SharingId);
        Assert.Equal(SharingScope.Shared, clone.SharingScope);
        Assert.Equal("Shopping", clone.Name);
    }

    // ----------------------------------------------------------------
    // Subtree with FK children
    // ----------------------------------------------------------------

    [Fact]
    public async Task TransferSubtree_ParentWithChildren_RemapsForeignKeys()
    {
        var parent = new TestList
        {
            Name = "Groceries",
            SharingScope = SharingScope.Shared,
            SharingId = "group-a:v1"
        };
        _context.TestLists.Add(parent);
        await _context.SaveChangesAsync();

        var item1 = new TestListItem
        {
            ListId = parent.Id, ItemName = "Milk", Quantity = 2,
            SharingScope = SharingScope.Shared, SharingId = "group-a:v1"
        };
        var item2 = new TestListItem
        {
            ListId = parent.Id, ItemName = "Bread", Quantity = 1,
            SharingScope = SharingScope.Shared, SharingId = "group-a:v1"
        };
        var note = new TestListNote
        {
            ListId = parent.Id, Text = "Don't forget coupons",
            SharingScope = SharingScope.Shared, SharingId = "group-a:v1"
        };
        _context.TestListItems.Add(item1);
        _context.TestListItems.Add(item2);
        _context.TestListNotes.Add(note);
        await _context.SaveChangesAsync();

        var idMap = await _transfer.TransferSubtreeAsync(
            "TestLists", parent.Id, "group-b:v1", SharingScope.Shared);

        // 4 entities transferred: parent + 2 items + 1 note.
        Assert.Equal(4, idMap.Count);

        // All source rows tombstoned.
        var sourceParent = await _context.TestLists
            .IgnoreQueryFilters().SingleAsync(l => l.Id == parent.Id);
        Assert.True(sourceParent.IsDeleted);

        var sourceItems = await _context.TestListItems
            .IgnoreQueryFilters().Where(i => i.ListId == parent.Id).ToListAsync();
        Assert.Equal(2, sourceItems.Count);
        Assert.All(sourceItems, i => Assert.True(i.IsDeleted));

        // Clone parent.
        var newParentId = idMap[parent.Id];
        var cloneParent = await _context.TestLists
            .IgnoreQueryFilters().SingleAsync(l => l.Id == newParentId);
        Assert.Equal("Groceries", cloneParent.Name);
        Assert.Equal("group-b:v1", cloneParent.SharingId);

        // Clone children: FKs remapped to new parent Id.
        var cloneItems = await _context.TestListItems
            .IgnoreQueryFilters()
            .Where(i => i.ListId == newParentId)
            .ToListAsync();
        Assert.Equal(2, cloneItems.Count);
        Assert.Contains(cloneItems, i => i.ItemName == "Milk");
        Assert.Contains(cloneItems, i => i.ItemName == "Bread");
        Assert.All(cloneItems, i =>
        {
            Assert.Equal("group-b:v1", i.SharingId);
            Assert.False(i.IsDeleted);
        });

        var cloneNotes = await _context.TestListNotes
            .IgnoreQueryFilters()
            .Where(n => n.ListId == newParentId)
            .ToListAsync();
        Assert.Single(cloneNotes);
        Assert.Equal("Don't forget coupons", cloneNotes[0].Text);
        Assert.Equal("group-b:v1", cloneNotes[0].SharingId);
    }

    // ----------------------------------------------------------------
    // Immutability invariant
    // ----------------------------------------------------------------

    [Fact]
    public async Task TransferSubtree_SourceSharingIdUnchanged()
    {
        var list = new TestList
        {
            Name = "Immutable check",
            SharingScope = SharingScope.Shared,
            SharingId = "source-group:v1"
        };
        _context.TestLists.Add(list);
        await _context.SaveChangesAsync();

        await _transfer.TransferSubtreeAsync(
            "TestLists", list.Id, "target-group:v1");

        var source = await _context.TestLists
            .IgnoreQueryFilters().SingleAsync(l => l.Id == list.Id);
        Assert.Equal("source-group:v1", source.SharingId);
    }

    // ----------------------------------------------------------------
    // Edge cases
    // ----------------------------------------------------------------

    [Fact]
    public async Task TransferSubtree_NonExistentRow_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _transfer.TransferSubtreeAsync("TestLists", Guid.NewGuid(), "target:v1"));
    }

    [Fact]
    public async Task TransferSubtree_EmptyTargetSharingId_Throws()
    {
        var list = new TestList { Name = "Bad target", SharingScope = SharingScope.Public };
        _context.TestLists.Add(list);
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _transfer.TransferSubtreeAsync("TestLists", list.Id, ""));
    }

    [Fact]
    public async Task TransferSubtree_AlreadyDeletedRow_Throws()
    {
        var list = new TestList
        {
            Name = "Already gone",
            SharingScope = SharingScope.Shared,
            SharingId = "group-a:v1"
        };
        _context.TestLists.Add(list);
        await _context.SaveChangesAsync();

        // Soft-delete it first.
        list.IsDeleted = true;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _transfer.TransferSubtreeAsync("TestLists", list.Id, "target:v1"));
    }
}
