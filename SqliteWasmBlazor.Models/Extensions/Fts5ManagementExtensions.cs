using Microsoft.EntityFrameworkCore;

namespace SqliteWasmBlazor.Models.Extensions;

/// <summary>
/// Extension methods for managing FTS5 indexes
/// </summary>
public static class Fts5ManagementExtensions
{
    /// <summary>
    /// Rebuilds the FTS5 index from the TodoItems table.
    /// Use this for maintenance or to fix index inconsistencies.
    /// </summary>
    /// <param name="dbContext">The database context</param>
    public static async ValueTask RebuildTodoItemFts5IndexAsync(this TodoDbContext dbContext)
    {
        // FTS5 'rebuild' command refreshes the index from the content table
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO FTSTodoItem(FTSTodoItem) VALUES('rebuild')");
    }

    /// <summary>
    /// Optimizes the FTS5 index to reduce size and improve query performance.
    /// This merges internal FTS5 b-tree structures.
    /// </summary>
    /// <param name="dbContext">The database context</param>
    public static async ValueTask OptimizeTodoItemFts5IndexAsync(this TodoDbContext dbContext)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO FTSTodoItem(FTSTodoItem) VALUES('optimize')");
    }

    /// <summary>
    /// Checks the integrity of the FTS5 index.
    /// Throws an exception if corruption is detected.
    /// </summary>
    /// <param name="dbContext">The database context</param>
    public static async ValueTask CheckTodoItemFts5IntegrityAsync(this TodoDbContext dbContext)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO FTSTodoItem(FTSTodoItem) VALUES('integrity-check')");
    }
}
