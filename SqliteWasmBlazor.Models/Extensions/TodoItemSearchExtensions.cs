using System.Linq;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.Models.Extensions;

/// <summary>
/// Defines how FTS5 queries should be processed
/// </summary>
public enum Fts5QueryMode
{
    /// <summary>
    /// Process query by stripping non-indexable characters, tokenizing by space,
    /// and building "token1* AND token2* ..." format for prefix matching
    /// </summary>
    Processed,

    /// <summary>
    /// Use query as-is without any processing (raw FTS5 syntax)
    /// </summary>
    Raw
}

/// <summary>
/// Extension methods for full-text search on TodoItem using FTS5
/// </summary>
public static class TodoItemSearchExtensions
{
    /// <summary>
    /// Processes a search query by stripping non-indexable characters, tokenizing by space,
    /// and building a query with prefix matching on each token (e.g., "token1* AND token2* ...").
    /// This is useful for search-as-you-type scenarios.
    /// </summary>
    /// <param name="query">The raw search query</param>
    /// <returns>Processed FTS5 query with prefix matching</returns>
    private static string ProcessQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return query;
        }

        // FTS5 reserved words that should be filtered out
        var reservedWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AND", "OR", "NOT"
        };

        // Strip all non-alphanumeric characters except spaces
        // FTS5's default tokenizer only indexes alphanumeric characters
        var cleaned = new string(query.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray());

        // Tokenize by whitespace and filter out empty tokens and reserved words
        var tokens = cleaned.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(t => !string.IsNullOrWhiteSpace(t))
                            .Select(t => t.Trim())
                            .Where(t => !reservedWords.Contains(t))  // Filter out FTS5 reserved words
                            .ToArray();

        // If no valid tokens, return empty query
        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        // Build "token1* AND token2* ..." format for prefix matching
        return string.Join(" AND ", tokens.Select(t => $"{t}*"));
    }

    /// <summary>
    /// Sanitizes a search query for FTS5 by escaping special characters while preserving:
    /// - Prefix matching (e.g., "ac10*")
    /// - Boolean operators (AND, OR, NOT)
    /// - Phrase queries (quoted strings)
    /// Special characters like #, -, (, ) are escaped unless they're part of FTS5 syntax.
    /// Note: FTS5's tokenizer strips punctuation like # during indexing, but searching for "#26"
    /// still works because FTS5 also strips it from the query.
    /// This method is used for Raw query mode.
    /// </summary>
    private static string SanitizeFts5Query(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return query;
        }

        // Check if query contains FTS5 boolean operators (case-insensitive)
        var hasBooleanOperators = query.Contains(" AND ", StringComparison.OrdinalIgnoreCase) ||
                                   query.Contains(" OR ", StringComparison.OrdinalIgnoreCase) ||
                                   query.Contains(" NOT ", StringComparison.OrdinalIgnoreCase);

        // Check if query ends with * (prefix search intent)
        var hasPrefixOperator = query.EndsWith('*');

        // If query has boolean operators, return as-is (user wants FTS5 syntax)
        // FTS5 handles these operators natively
        if (hasBooleanOperators)
        {
            return query;
        }

        // Otherwise, sanitize for safe literal search
        var baseQuery = hasPrefixOperator ? query[..^1] : query;

        // Escape double quotes by doubling them (FTS5 phrase syntax)
        var sanitized = baseQuery.Replace("\"", "\"\"");

        // Escape other special FTS5 characters that could cause syntax errors
        // Keep * separate as it's handled above for prefix matching
        sanitized = sanitized.Replace("(", "\\(")
                             .Replace(")", "\\)")
                             .Replace("-", "\\-");

        // If query has prefix operator, use it without quotes to enable prefix matching
        // Otherwise wrap in quotes for exact phrase search (safer for special chars like #)
        if (hasPrefixOperator)
        {
            return $"{sanitized}*";
        }
        else
        {
            return $"\"{sanitized}\"";
        }
    }

    /// <summary>
    /// Prepares a search query based on the specified query mode
    /// </summary>
    /// <param name="query">The raw search query</param>
    /// <param name="mode">The query processing mode</param>
    /// <returns>Prepared query for FTS5</returns>
    private static string PrepareQuery(string query, Fts5QueryMode mode)
    {
        return mode switch
        {
            Fts5QueryMode.Processed => ProcessQuery(query),
            Fts5QueryMode.Raw => query,
            _ => ProcessQuery(query) // Default to Processed
        };
    }

    /// <summary>
    /// Performs full-text search on TodoItem Title and Description using FTS5
    /// </summary>
    /// <param name="dbContext">The database context</param>
    /// <param name="searchQuery">The search query (supports FTS5 syntax)</param>
    /// <param name="queryMode">The query processing mode (default: Processed)</param>
    /// <returns>IQueryable of matching TodoItems ordered by relevance (rank)</returns>
    public static IQueryable<TodoItem> SearchTodoItems(
        this TodoDbContext dbContext,
        string searchQuery,
        Fts5QueryMode queryMode = Fts5QueryMode.Processed)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return dbContext.TodoItems.AsQueryable();
        }

        // Prepare query based on the specified mode
        var preparedQuery = PrepareQuery(searchQuery, queryMode);

        // If prepared query is empty (e.g., all tokens were stripped), return empty result
        if (string.IsNullOrWhiteSpace(preparedQuery))
        {
            return dbContext.TodoItems.Where(t => false).AsQueryable();
        }

        // Query FTS5 table and join with TodoItems
        // The Match property is used with equality to generate the FTS5 MATCH operator
        return dbContext.FTSTodoItems
            .Where(fts => fts.Match == preparedQuery)
            .OrderBy(fts => fts.Rank)
            .Select(fts => fts.TodoItem!)
            .AsNoTracking();
    }

    /// <summary>
    /// Performs full-text search with highlighted results using FormattableString and SqlQuery
    /// Note: Cannot use LINQ + DbFunction due to EF Core generating subquery that breaks FTS5 highlight()
    /// </summary>
    /// <param name="dbContext">The database context</param>
    /// <param name="searchQuery">The search query (supports FTS5 syntax)</param>
    /// <param name="highlightOpen">Opening tag for highlighting (e.g., "&lt;mark&gt;")</param>
    /// <param name="highlightClose">Closing tag for highlighting (e.g., "&lt;/mark&gt;")</param>
    /// <param name="queryMode">The query processing mode (default: Processed)</param>
    /// <returns>IQueryable of search results with highlighting</returns>
    public static IQueryable<TodoItemSearchResult> SearchTodoItemsWithHighlight(
        this TodoDbContext dbContext,
        string searchQuery,
        string highlightOpen = "<mark>",
        string highlightClose = "</mark>",
        Fts5QueryMode queryMode = Fts5QueryMode.Processed)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return dbContext.TodoItems
                .Select(item => new TodoItemSearchResult
                {
                    Id = item.Id,
                    Title = item.Title,
                    Description = item.Description,
                    CreatedAt = item.CreatedAt,
                    IsCompleted = item.IsCompleted,
                    CompletedAt = item.CompletedAt,
                    HighlightedTitle = null,
                    HighlightedDescription = null,
                    Rank = 0
                });
        }

        // Prepare query based on the specified mode
        var preparedQuery = PrepareQuery(searchQuery, queryMode);

        // If prepared query is empty (e.g., all tokens were stripped), return empty result
        if (string.IsNullOrWhiteSpace(preparedQuery))
        {
            return dbContext.TodoItems
                .Where(t => false)
                .Select(item => new TodoItemSearchResult
                {
                    Id = item.Id,
                    Title = item.Title,
                    Description = item.Description,
                    CreatedAt = item.CreatedAt,
                    IsCompleted = item.IsCompleted,
                    CompletedAt = item.CompletedAt,
                    HighlightedTitle = null,
                    HighlightedDescription = null,
                    Rank = 0
                });
        }

        // Use raw SQL with SqlQuery to avoid EF Core's subquery generation
        // This is necessary because FTS5's highlight() function must be called directly on the FTS table
        // SqlQuery (not SqlQueryRaw) provides automatic SQL injection protection
        return dbContext.Database
            .SqlQuery<TodoItemSearchResult>($@"
                SELECT
                    t.Id,
                    t.Title,
                    t.Description,
                    t.CreatedAt,
                    t.IsCompleted,
                    t.CompletedAt,
                    highlight(FTSTodoItem, 0, {highlightOpen}, {highlightClose}) AS HighlightedTitle,
                    highlight(FTSTodoItem, 1, {highlightOpen}, {highlightClose}) AS HighlightedDescription,
                    rank AS Rank
                FROM FTSTodoItem
                INNER JOIN TodoItems t ON FTSTodoItem.rowid = t.Id
                WHERE FTSTodoItem MATCH {preparedQuery}
                ORDER BY rank")
            .AsNoTracking();
    }

    /// <summary>
    /// Performs full-text search with snippets (excerpts) using raw SQL
    /// Note: Cannot use LINQ + DbFunction due to EF Core generating subquery that breaks FTS5 snippet()
    /// </summary>
    /// <param name="dbContext">The database context</param>
    /// <param name="searchQuery">The search query (supports FTS5 syntax)</param>
    /// <param name="snippetOpen">Opening tag for highlighting in snippet</param>
    /// <param name="snippetClose">Closing tag for highlighting in snippet</param>
    /// <param name="ellipsis">Ellipsis string (e.g., "...")</param>
    /// <param name="maxTokens">Maximum number of tokens in snippet</param>
    /// <param name="queryMode">The query processing mode (default: Processed)</param>
    /// <returns>IQueryable of search results with snippets</returns>
    public static IQueryable<TodoItemSnippetResult> SearchTodoItemsWithSnippet(
        this TodoDbContext dbContext,
        string searchQuery,
        string snippetOpen = "<mark>",
        string snippetClose = "</mark>",
        string ellipsis = "...",
        int maxTokens = 10,
        Fts5QueryMode queryMode = Fts5QueryMode.Processed)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return dbContext.TodoItems
                .Select(item => new TodoItemSnippetResult
                {
                    Id = item.Id,
                    Title = item.Title,
                    Description = item.Description,
                    CreatedAt = item.CreatedAt,
                    IsCompleted = item.IsCompleted,
                    CompletedAt = item.CompletedAt,
                    TitleSnippet = null,
                    DescriptionSnippet = item.Description.Length > 100
                        ? item.Description.Substring(0, 100) + "..."
                        : item.Description,
                    Rank = 0
                });
        }

        // Prepare query based on the specified mode
        var preparedQuery = PrepareQuery(searchQuery, queryMode);

        // If prepared query is empty (e.g., all tokens were stripped), return empty result
        if (string.IsNullOrWhiteSpace(preparedQuery))
        {
            return dbContext.TodoItems
                .Where(t => false)
                .Select(item => new TodoItemSnippetResult
                {
                    Id = item.Id,
                    Title = item.Title,
                    Description = item.Description,
                    CreatedAt = item.CreatedAt,
                    IsCompleted = item.IsCompleted,
                    CompletedAt = item.CompletedAt,
                    TitleSnippet = null,
                    DescriptionSnippet = item.Description.Length > 100
                        ? item.Description.Substring(0, 100) + "..."
                        : item.Description,
                    Rank = 0
                });
        }

        // Use raw SQL with SqlQuery to avoid EF Core's subquery generation
        // snippet() function requires direct FTS5 table cursor access (same limitation as highlight())
        // SqlQuery (not SqlQueryRaw) provides automatic SQL injection protection
        // Note: We select snippet columns for display, NOT the full Title/Description
        return dbContext.Database
            .SqlQuery<TodoItemSnippetResult>($@"
                SELECT
                    t.Id,
                    snippet(FTSTodoItem, 0, {snippetOpen}, {snippetClose}, {ellipsis}, {maxTokens}) AS Title,
                    snippet(FTSTodoItem, 1, {snippetOpen}, {snippetClose}, {ellipsis}, {maxTokens}) AS Description,
                    t.CreatedAt,
                    t.IsCompleted,
                    t.CompletedAt,
                    snippet(FTSTodoItem, 0, {snippetOpen}, {snippetClose}, {ellipsis}, {maxTokens}) AS TitleSnippet,
                    snippet(FTSTodoItem, 1, {snippetOpen}, {snippetClose}, {ellipsis}, {maxTokens}) AS DescriptionSnippet,
                    rank AS Rank
                FROM FTSTodoItem
                INNER JOIN TodoItems t ON FTSTodoItem.rowid = t.Id
                WHERE FTSTodoItem MATCH {preparedQuery}
                ORDER BY rank")
            .AsNoTracking();
    }
}

/// <summary>
/// Result type for search with highlighting - inherits from TodoItem to avoid data duplication
/// </summary>
public class TodoItemSearchResult : TodoItem
{
    /// <summary>
    /// Hides inherited navigation property (not supported by SqlQueryRaw)
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public new FTSTodoItem? FTS { get; set; }

    public string? HighlightedTitle { get; set; }
    public string? HighlightedDescription { get; set; }
    public double Rank { get; set; }

    /// <summary>
    /// Gets the title to display, preferring highlighted version if available
    /// </summary>
    public string DisplayTitle => HighlightedTitle ?? Title;

    /// <summary>
    /// Gets the description to display, preferring highlighted version if available
    /// </summary>
    public string DisplayDescription => HighlightedDescription ?? Description;
}

/// <summary>
/// Result type for search with snippets - inherits from TodoItem to avoid data duplication
/// </summary>
public class TodoItemSnippetResult : TodoItem
{
    /// <summary>
    /// Hides inherited navigation property (not supported by SqlQueryRaw)
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public new FTSTodoItem? FTS { get; set; }

    public string? TitleSnippet { get; set; }
    public string? DescriptionSnippet { get; set; }
    public double Rank { get; set; }

    /// <summary>
    /// Gets the title snippet to display, preferring snippet if available
    /// </summary>
    public string DisplayTitleSnippet => TitleSnippet ?? Title;

    /// <summary>
    /// Gets the description snippet to display, preferring snippet if available
    /// </summary>
    public string DisplayDescriptionSnippet => DescriptionSnippet ?? Description;
}
