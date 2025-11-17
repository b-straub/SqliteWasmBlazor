using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqliteWasmBlazor.Models.Models;

public class TodoItem
{
    [Key]
    [Column(TypeName = "BLOB")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Soft delete flag for delta sync tombstones
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Timestamp when item was soft-deleted
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Navigation property to FTS5 virtual table for full-text search
    /// </summary>
    public FTSTodoItem? FTS { get; set; }
}
