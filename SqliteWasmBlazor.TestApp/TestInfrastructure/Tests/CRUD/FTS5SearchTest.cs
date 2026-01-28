using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.Extensions;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.CRUD;

internal class FTS5SearchTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "FTS5_Search";

    // FTS5 requires migrations (not EnsureCreated) because the virtual table is created via migration SQL
    protected override bool AutoCreateDatabase => false;

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        // CRITICAL: Use MigrateAsync (not EnsureCreated) to create FTS5 virtual table and triggers
        // EnsureCreated only creates tables from entity models and ignores migration SQL
        // FTS5 requires the raw SQL in the migration (CREATE VIRTUAL TABLE USING fts5)
        await context.Database.MigrateAsync();

        // Create test data with various search scenarios
        var testItems = new[]
        {
            new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = "Buy groceries",
                Description = "Get milk, eggs, and bread from the store",
                IsCompleted = false,
                UpdatedAt = DateTime.UtcNow
            },
            new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = "Write report",
                Description = "Complete the quarterly financial report",
                IsCompleted = false,
                UpdatedAt = DateTime.UtcNow
            },
            new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = "Call dentist",
                Description = "Schedule appointment for teeth cleaning",
                IsCompleted = true,
                UpdatedAt = DateTime.UtcNow.AddDays(-1),
                CompletedAt = DateTime.UtcNow
            },
            new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = "Meeting preparation",
                Description = "Prepare slides for the quarterly review meeting",
                IsCompleted = false,
                UpdatedAt = DateTime.UtcNow
            }
        };

        context.TodoItems.AddRange(testItems);
        await context.SaveChangesAsync();

        // Wait for FTS5 triggers to sync (small delay)
        await Task.Delay(100);

        // Test 1: Search for "quarterly"
        var quarterlyResults = await context
            .SearchTodoItems("quarterly")
            .ToListAsync();

        if (quarterlyResults.Count != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 results for 'quarterly', got {quarterlyResults.Count}");
        }

        // Test 2: Search for "report"
        var reportResults = await context
            .SearchTodoItems("report")
            .ToListAsync();

        if (reportResults.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 result for 'report', got {reportResults.Count}");
        }

        // Test 3: Search with highlighting
        var highlightedResults = await context
            .SearchTodoItemsWithHighlight("quarterly", "<b>", "</b>")
            .ToListAsync();

        if (highlightedResults.Count != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 highlighted results, got {highlightedResults.Count}");
        }

        var firstHighlighted = highlightedResults.First();
        if (firstHighlighted.HighlightedDescription is null ||
            !firstHighlighted.HighlightedDescription.Contains("<b>") ||
            !firstHighlighted.HighlightedDescription.Contains("</b>"))
        {
            throw new InvalidOperationException("Highlighting not applied correctly");
        }

        // Test 4: Search with snippets
        var snippetResults = await context
            .SearchTodoItemsWithSnippet("meeting", "<mark>", "</mark>", "...", 5)
            .ToListAsync();

        if (snippetResults.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 snippet result, got {snippetResults.Count}");
        }

        var firstSnippet = snippetResults.First();
        if (firstSnippet.DescriptionSnippet is null ||
            !firstSnippet.DescriptionSnippet.Contains("<mark>"))
        {
            throw new InvalidOperationException("Snippet not applied correctly");
        }

        // Test 5: Empty search should return all items
        var allResults = await context
            .SearchTodoItems("")
            .ToListAsync();

        if (allResults.Count != 4)
        {
            throw new InvalidOperationException(
                $"Expected 4 results for empty search, got {allResults.Count}");
        }

        // Test 6: Search with AND operator (FTS5 syntax) using Raw mode
        var andResults = await context
            .SearchTodoItems("quarterly AND report", Fts5QueryMode.Raw)
            .ToListAsync();

        if (andResults.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 result for 'quarterly AND report' (Raw), got {andResults.Count}");
        }

        // Test 7: Processed mode - tokenize and add wildcards
        // Search for "quar repo" should match "quarterly" and "report"
        var processedResults = await context
            .SearchTodoItems("quar repo")
            .ToListAsync();

        if (processedResults.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 result for 'quar repo' (Processed), got {processedResults.Count}");
        }

        // Test 8: Processed mode with special characters (should be stripped)
        // "#quarter#ly #report#" should become "quarterly* AND report*"
        var processedSpecialChars = await context
            .SearchTodoItems("#quarter#ly #report#")
            .ToListAsync();

        if (processedSpecialChars.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 result for '#quarter#ly #report#' (Processed), got {processedSpecialChars.Count}");
        }

        // Test 9: Processed mode - partial matching with single word
        var processedSingleWord = await context
            .SearchTodoItems("gro")
            .ToListAsync();

        if (processedSingleWord.Count != 1) // Should match "groceries"
        {
            throw new InvalidOperationException(
                $"Expected 1 result for 'gro' (Processed), got {processedSingleWord.Count}");
        }

        // Test 10: Raw mode with exact term (no wildcards)
        var rawExact = await context
            .SearchTodoItems("groceries", Fts5QueryMode.Raw)
            .ToListAsync();

        if (rawExact.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 result for 'groceries' (Raw), got {rawExact.Count}");
        }

        return "OK";
    }
}
