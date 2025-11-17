namespace SqliteWasmBlazor.Models.Models;

/// <summary>
/// Filter options for TodoItem queries
/// </summary>
public class TodoItemFilterOptions
{
    /// <summary>
    /// Only include items created/updated after this date
    /// </summary>
    public DateTime? UpdatedAfter { get; set; }

    /// <summary>
    /// Only include completed items
    /// </summary>
    public bool? IsCompleted { get; set; }

    /// <summary>
    /// Search term for title/description
    /// </summary>
    public string? SearchTerm { get; set; }

    /// <summary>
    /// Maximum number of items to return (null = all)
    /// </summary>
    public int? Limit { get; set; }

    /// <summary>
    /// Include soft-deleted items in results (default: false)
    /// </summary>
    public bool IncludeDeleted { get; set; }

    /// <summary>
    /// Creates default filter for delta exports (last 24 hours)
    /// </summary>
    public static TodoItemFilterOptions CreateDefaultDeltaFilter()
    {
        return new TodoItemFilterOptions
        {
            UpdatedAfter = DateTime.UtcNow.AddDays(-1)
        };
    }
}
