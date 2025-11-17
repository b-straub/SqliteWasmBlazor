using Microsoft.EntityFrameworkCore.Migrations;

namespace SqliteWasmBlazor.Models.Extensions;

/// <summary>
/// Extension methods for MigrationBuilder to handle FTS5 virtual table setup
/// </summary>
public static class MigrationBuilderExtensions
{
    /// <summary>
    /// Creates FTS5 virtual table and triggers for TodoItems full-text search.
    /// Call this in migration Up() method after creating TodoItems table.
    /// </summary>
    public static void CreateTodoItemsFts5(this MigrationBuilder migrationBuilder)
    {
        // Create FTS5 virtual table
        migrationBuilder.Sql(@"
            CREATE VIRTUAL TABLE FTSTodoItem USING fts5(
                Id UNINDEXED,
                Title,
                Description,
                content='TodoItems',
                content_rowid='rowid'
            )");

        // INSERT trigger - only index non-deleted items
        migrationBuilder.Sql(@"
            CREATE TRIGGER TodoItems_ai AFTER INSERT ON TodoItems BEGIN
                INSERT INTO FTSTodoItem(rowid, Id, Title, Description)
                SELECT rowid, Id, Title, Description FROM TodoItems WHERE rowid = new.rowid AND IsDeleted = 0;
            END");

        // DELETE trigger
        migrationBuilder.Sql(@"
            CREATE TRIGGER TodoItems_ad AFTER DELETE ON TodoItems BEGIN
                INSERT INTO FTSTodoItem(FTSTodoItem, rowid, Id, Title, Description)
                VALUES('delete', old.rowid, old.Id, old.Title, old.Description);
            END");

        // UPDATE trigger - only index non-deleted items
        migrationBuilder.Sql(@"
            CREATE TRIGGER TodoItems_au AFTER UPDATE ON TodoItems BEGIN
                INSERT INTO FTSTodoItem(FTSTodoItem, rowid, Id, Title, Description)
                VALUES('delete', old.rowid, old.Id, old.Title, old.Description);
                INSERT INTO FTSTodoItem(rowid, Id, Title, Description)
                SELECT rowid, Id, Title, Description FROM TodoItems WHERE rowid = new.rowid AND IsDeleted = 0;
            END");
    }

    /// <summary>
    /// Drops FTS5 virtual table and triggers for TodoItems.
    /// Call this in migration Down() method.
    /// </summary>
    public static void DropTodoItemsFts5(this MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS TodoItems_au");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS TodoItems_ad");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS TodoItems_ai");
        migrationBuilder.Sql("DROP TABLE IF EXISTS FTSTodoItem");
    }
}
