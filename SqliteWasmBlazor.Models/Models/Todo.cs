using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqliteWasmBlazor.Models.Models;

[Table("todos")]
public class Todo
{
    [Key]
    [Column(TypeName = "binary(16)")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(255)]
    public required string Title { get; set; }

    [MaxLength(255)]
    public string? Description { get; set; }

    public DateTime? DueDate { get; set; }

    [Required]
    public bool Completed { get; set; } = false;

    [Required]
    public int Priority { get; set; } = 0;

    [Required]
    [Column(TypeName = "binary(16)")]
    public required Guid TodoListId { get; set; }

    public DateTime? CompletedAt { get; set; }

    public TodoList? TodoList { get; set; }
}
