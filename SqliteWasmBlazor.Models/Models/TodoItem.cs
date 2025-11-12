using System.ComponentModel.DataAnnotations;

namespace SqliteWasmBlazor.Models.Models;

public class TodoItem
{
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Navigation property to FTS5 virtual table for full-text search
    /// </summary>
    public FTSTodoItem? FTS { get; set; }
}
