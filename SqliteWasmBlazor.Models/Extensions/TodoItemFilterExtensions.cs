using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.Models.Extensions;

/// <summary>
/// Extension methods for filtering TodoItems
/// </summary>
public static class TodoItemFilterExtensions
{
    /// <summary>
    /// Applies filter options to a TodoItem query
    /// </summary>
    public static IQueryable<TodoItem> ApplyFilters(this IQueryable<TodoItem> query, TodoItemFilterOptions? filters)
    {
        if (filters is null)
        {
            // Default: exclude soft-deleted items
            return query.Where(t => !t.IsDeleted);
        }

        // Exclude soft-deleted items unless explicitly requested
        if (!filters.IncludeDeleted)
        {
            query = query.Where(t => !t.IsDeleted);
        }

        if (filters.UpdatedAfter.HasValue)
        {
            query = query.Where(t => t.UpdatedAt >= filters.UpdatedAfter.Value);
        }

        if (filters.IsCompleted.HasValue)
        {
            query = query.Where(t => t.IsCompleted == filters.IsCompleted.Value);
        }

        if (!string.IsNullOrWhiteSpace(filters.SearchTerm))
        {
            var searchTerm = filters.SearchTerm.ToLower();
            query = query.Where(t =>
                t.Title.ToLower().Contains(searchTerm) ||
                t.Description.ToLower().Contains(searchTerm));
        }

        if (filters.Limit.HasValue)
        {
            query = query.Take(filters.Limit.Value);
        }

        return query;
    }
}
