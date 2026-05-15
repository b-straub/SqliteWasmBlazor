using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SqliteWasmBlazor;

/// <summary>
/// Generic schema validation for any DbContext after raw database import.
/// Derives expected table names from the EF model metadata and checks sqlite_master.
/// </summary>
public static class SchemaValidationExtensions
{
    /// <summary>
    /// Validates that the database contains the tables defined in the EF model.
    /// Uses the design-time model to access IsTableExcludedFromMigrations
    /// (not available on the read-optimized runtime model).
    /// Skips owned entities and entities excluded from migrations (e.g., FTS5 virtual tables).
    /// </summary>
    /// <param name="context">The database context connected to the imported database.</param>
    /// <param name="databaseDisplayName">Display name for error messages (e.g., "TodoDb.db").</param>
    /// <exception cref="InvalidOperationException">Thrown when required tables are missing.</exception>
    public static async Task ValidateImportedSchemaAsync(this DbContext context, string databaseDisplayName)
    {
        var designTimeModel = context.GetService<IDesignTimeModel>().Model;

        var requiredTables = designTimeModel.GetEntityTypes()
            .Where(e => !e.IsOwned()
                        && e.GetTableName() is not null
                        && !e.IsTableExcludedFromMigrations())
            .Select(e => e.GetTableName()!)
            .Distinct()
            .ToArray();

        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";

        var tables = new HashSet<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        var missingTables = requiredTables.Where(t => !tables.Contains(t)).ToArray();
        if (missingTables.Length > 0)
        {
            throw new InvalidOperationException(
                $"Incompatible database: missing tables {string.Join(", ", missingTables)}. " +
                $"The file is not a valid {databaseDisplayName} database.");
        }
    }
}
