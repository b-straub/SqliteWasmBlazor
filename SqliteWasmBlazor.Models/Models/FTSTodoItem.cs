using System.ComponentModel.DataAnnotations.Schema;

namespace SqliteWasmBlazor.Models.Models;

/// <summary>
/// FTS5 virtual table for full-text search on TodoItem Title and Description
/// </summary>
[Table("FTSTodoItem")]
public class FTSTodoItem
{
    /// <summary>
    /// RowId maps to TodoItem.Id for the one-to-one relationship
    /// </summary>
    public int RowId { get; set; }

    /// <summary>
    /// Navigation property to the original TodoItem
    /// </summary>
    public TodoItem? TodoItem { get; set; }

    /// <summary>
    /// Title content for FTS5 indexing
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Description content for FTS5 indexing
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Virtual column for MATCH queries. Maps to table name for FTS5.
    /// </summary>
    [Column("FTSTodoItem")]
    public string Match { get; set; } = string.Empty;

    /// <summary>
    /// Virtual column for ranking search results by relevance
    /// </summary>
    public double? Rank { get; set; }
}
