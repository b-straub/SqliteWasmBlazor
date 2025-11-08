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
    public DbSet<TypeTestEntity> TypeTests { get; set; }
    public DbSet<TodoList> TodoLists { get; set; }
    public DbSet<Todo> Todos { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TodoItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.CreatedAt).IsRequired();
        });

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
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(255);
            entity.Property(e => e.IsActive).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();

            // Configure one-to-many relationship
            entity.HasMany(e => e.Todos)
                .WithOne(e => e.TodoList)
                .HasForeignKey(e => e.TodoListId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Todo>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Description).HasMaxLength(255);
            entity.Property(e => e.TodoListId).IsRequired();
            entity.Property(e => e.Completed).IsRequired();
            entity.Property(e => e.Priority).IsRequired();
        });
    }
}
