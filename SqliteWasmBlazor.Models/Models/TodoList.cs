using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqliteWasmBlazor.Models.Models;

[Table("todoLists")]
public class TodoList
{
    [Key]
    [Column(TypeName = "BLOB")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(255)]
    public required string Title { get; set; }

    [Required]
    public bool IsActive { get; set; } = true;

    [Required]
    public DateTime CreatedAt { get; set; }

    public ICollection<Todo> Todos { get; } = new List<Todo>();
}
