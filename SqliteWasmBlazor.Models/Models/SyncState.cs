using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqliteWasmBlazor.Models.Models;

/// <summary>
/// Tracks checkpoint history for delta sync and rollback capability.
/// Each row represents a checkpoint snapshot of the database state.
/// Latest row is the current sync state.
/// </summary>
public class SyncState
{
    /// <summary>
    /// Auto-increment primary key
    /// </summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Timestamp when this checkpoint was created (UTC).
    /// For delta sync, this is used to filter items for incremental sync.
    /// </summary>
    [Required]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Optional description/name for the checkpoint
    /// </summary>
    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Number of active (non-deleted) items at checkpoint creation
    /// </summary>
    public int ActiveItemCount { get; set; }

    /// <summary>
    /// Number of soft-deleted items (tombstones) at checkpoint creation
    /// </summary>
    public int TombstoneCount { get; set; }

    /// <summary>
    /// Type of checkpoint: "Auto" (after delta export), "Manual", or "Initial"
    /// </summary>
    [MaxLength(50)]
    public string CheckpointType { get; set; } = "Auto";
}
