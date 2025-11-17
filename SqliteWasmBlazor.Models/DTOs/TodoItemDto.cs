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
    public Guid Id { get; set; }

    [Key(1)]
    public string Title { get; set; } = string.Empty;

    [Key(2)]
    public string Description { get; set; } = string.Empty;

    [Key(3)]
    public bool IsCompleted { get; set; }

    [Key(4)]
    public DateTime UpdatedAt { get; set; }

    [Key(5)]
    public DateTime? CompletedAt { get; set; }

    [Key(6)]
    public bool IsDeleted { get; set; }

    [Key(7)]
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Convert DTO to entity model
    /// </summary>
    public TodoItem ToEntity() => new()
    {
        Id = Id,
        Title = Title,
        Description = Description,
        IsCompleted = IsCompleted,
        UpdatedAt = UpdatedAt,
        CompletedAt = CompletedAt,
        IsDeleted = IsDeleted,
        DeletedAt = DeletedAt
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
        UpdatedAt = entity.UpdatedAt,
        CompletedAt = entity.CompletedAt,
        IsDeleted = entity.IsDeleted,
        DeletedAt = entity.DeletedAt
    };
}
