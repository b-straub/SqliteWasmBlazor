using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqliteWasm.Data.Models.Models;

[Table("todoLists")]
public class TodoList
{
    [Key]
    [Column(TypeName = "binary(16)")]
    public Guid Id { get; set; }

    [MaxLength(255)]
    public required string Title { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public ICollection<Todo> Todos { get; } = new List<Todo>();
}
