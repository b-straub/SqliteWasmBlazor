using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.Models;

public class TodoDbContext : DbContext
{
    public TodoDbContext(DbContextOptions<TodoDbContext> options) : base(options)
    {
    }

    public DbSet<TodoItem> TodoItems { get; set; }
    public DbSet<FTSTodoItem> FTSTodoItems { get; set; }
    public DbSet<TypeTestEntity> TypeTests { get; set; }
    public DbSet<TodoList> TodoLists { get; set; }
    public DbSet<Todo> Todos { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TypeTestEntity>(entity =>
        {
            // Configure JSON serialization for List<int>
            entity.Property(e => e.IntList)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<int>>(v, (JsonSerializerOptions?)null) ?? new List<int>()
                )
                .Metadata.SetValueComparer(
                    new ValueComparer<List<int>>(
                        (c1, c2) => c1!.SequenceEqual(c2!),
                        c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                        c => c.ToList()
                    )
                );
        });

        modelBuilder.Entity<TodoList>(entity =>
        {
            // Configure one-to-many relationship
            entity.HasMany(e => e.Todos)
                .WithOne(e => e.TodoList)
                .HasForeignKey(e => e.TodoListId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure FTS5 virtual table for TodoItem
        modelBuilder.Entity<FTSTodoItem>(entity =>
        {
            // RowId is the primary key (maps to SQLite rowid)
            entity.HasKey(fts => fts.RowId);

            // Match property maps to the table name (FTS5 requirement)
            entity.Property(fts => fts.Match)
                .HasColumnName(nameof(FTSTodoItem));

            // Configure one-to-one relationship with TodoItem
            entity.HasOne(fts => fts.TodoItem)
                .WithOne(item => item.FTS)
                .HasForeignKey<FTSTodoItem>(fts => fts.RowId);

            // Mark as ToTable to prevent EF from trying to create it as a regular table
            entity.ToTable("FTSTodoItem");
        });
    }

    /// <summary>
    /// Highlights matching text in FTS5 search results
    /// </summary>
    [Microsoft.EntityFrameworkCore.DbFunction]
    public static string Highlight(string match, int column, string open, string close)
    {
        throw new NotImplementedException("This method is translated to SQL by EF Core");
    }

    /// <summary>
    /// Extracts snippet from FTS5 search results
    /// </summary>
    [Microsoft.EntityFrameworkCore.DbFunction]
    public static string Snippet(string match, int column, string open, string close, string ellipsis, int tokens)
    {
        throw new NotImplementedException("This method is translated to SQL by EF Core");
    }
}
