using MessagePack;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.CRUD;

/// <summary>
/// Rows written by the bulk-import path must be addressable by EF Core's
/// Guid key parameters — the same invariant <c>Guid_HasDataSeedQuery</c>
/// guards for the ADO parameter path.
///
/// Both paths have to agree on one storage form for a Guid PK. The ADO
/// layer binds <c>WHERE "Id" = @p</c> as uppercase TEXT, so bulk import
/// must write uppercase TEXT too. When it doesn't, the rows list and
/// display correctly (no Id predicate is involved) while every
/// key-addressed operation silently matches nothing: <c>FindAsync</c>
/// returns null and <c>UPDATE ... WHERE Id = @p</c> affects zero rows.
/// </summary>
internal class BulkImportedRowsAreEfAddressableTest(
    IDbContextFactory<TodoDbContext> factory,
    ISqliteWasmDatabaseService databaseService)
    : SqliteWasmTest(factory, databaseService)
{
    public override string Name => "BulkImport_RowsAreEfAddressable";

    // Migrations (not EnsureCreated) so the FTS5 triggers are live, matching
    // the Demo's Administration-page generator.
    protected override bool AutoCreateDatabase => false;

    public override async ValueTask<string?> RunTestAsync()
    {
        if (DatabaseService is null)
        {
            throw new InvalidOperationException("DatabaseService is required for this test");
        }

        await using (var migrateContext = await Factory.CreateDbContextAsync())
        {
            await migrateContext.Database.MigrateAsync();
        }

        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var header = MessagePackFileHeaderV2.Create<TodoItemDto>(
            tableName: "TodoItems",
            primaryKeyColumn: "Id",
            recordCount: ids.Length,
            mode: 0);

        using var stream = new MemoryStream();
        MessagePackSerializer.Serialize(stream, header);
        for (var i = 0; i < ids.Length; i++)
        {
            MessagePackSerializer.Serialize(stream, new TodoItemDto
            {
                Id = ids[i],
                Title = $"Imported #{i + 1}",
                Description = "bulk-import round trip",
                UpdatedAt = DateTime.UtcNow,
                IsCompleted = false,
                IsDeleted = false,
            });
        }

        var imported = await DatabaseService.ImportRowsAsync(TestDatabaseName, stream.ToArray());
        if (imported != ids.Length)
        {
            throw new InvalidOperationException($"Expected {ids.Length} imported rows, got {imported}");
        }

        var target = ids[1];

        // The row must be reachable by its key — this is what the delete path does.
        await using (var findContext = await Factory.CreateDbContextAsync())
        {
            var tracked = await findContext.TodoItems.FindAsync([target], CancellationToken.None);
            if (tracked is null)
            {
                throw new InvalidOperationException(
                    $"FindAsync returned null for imported id {target} — imported rows are not EF-addressable.");
            }

            tracked.IsDeleted = true;
            tracked.DeletedAt = DateTime.UtcNow;

            var saved = await findContext.SaveChangesAsync();
            if (saved != 1)
            {
                throw new InvalidOperationException($"Expected SaveChangesAsync to write 1 row, got {saved}");
            }
        }

        // ... and by a LINQ predicate on the key.
        await using (var verifyContext = await Factory.CreateDbContextAsync())
        {
            var active = await verifyContext.TodoItems.CountAsync(t => !t.IsDeleted);
            if (active != ids.Length - 1)
            {
                throw new InvalidOperationException($"Expected {ids.Length - 1} active rows after delete, got {active}");
            }

            var byPredicate = await verifyContext.TodoItems
                .SingleOrDefaultAsync(t => t.Id == target);
            if (byPredicate is null)
            {
                throw new InvalidOperationException($"LINQ Where by Guid found no row for imported id {target}");
            }

            if (!byPredicate.IsDeleted)
            {
                throw new InvalidOperationException("IsDeleted was not persisted on the imported row");
            }
        }

        return "OK";
    }

    private const string TestDatabaseName = "TestDb.db";
}
