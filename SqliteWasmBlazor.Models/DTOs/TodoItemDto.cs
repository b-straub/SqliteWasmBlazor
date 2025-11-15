using MessagePack;
using SqliteWasmBlazor.Models.Models;

namespace SqliteWasmBlazor.Models.DTOs;

/// <summary>
/// Data Transfer Object for TodoItem with MessagePack serialization support
/// Used for efficient serialization/deserialization during import/export
/// </summary>
[MessagePackObject]
public class TodoItemDto
{
    [Key(0)]
    public int Id { get; set; }

    [Key(1)]
    public string Title { get; set; } = string.Empty;

    [Key(2)]
    public string Description { get; set; } = string.Empty;

    [Key(3)]
    public bool IsCompleted { get; set; }

    [Key(4)]
    public DateTime CreatedAt { get; set; }

    [Key(5)]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Convert DTO to entity model
    /// </summary>
    public TodoItem ToEntity() => new()
    {
        Id = Id,
        Title = Title,
        Description = Description,
        IsCompleted = IsCompleted,
        CreatedAt = CreatedAt,
        CompletedAt = CompletedAt
    };

    /// <summary>
    /// Create DTO from entity model
    /// </summary>
    public static TodoItemDto FromEntity(TodoItem entity) => new()
    {
        Id = entity.Id,
        Title = entity.Title,
        Description = entity.Description,
        IsCompleted = entity.IsCompleted,
        CreatedAt = entity.CreatedAt,
        CompletedAt = entity.CompletedAt
    };
}
