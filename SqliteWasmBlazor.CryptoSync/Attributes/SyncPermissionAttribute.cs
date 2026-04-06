namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Defines permission for a role on the entity's table.
/// Applied to SyncableEntity classes. Generator produces EF Core HasData() seed calls.
///
/// Example:
/// [SyncPermission(SyncRole.Viewer, "readonly", ReadWriteColumns = ["IsBought"])]
/// public class ShoppingItem : SyncableEntity { ... }
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class SyncPermissionAttribute : Attribute
{
    /// <summary>Which role this permission applies to.</summary>
    public SyncRole Role { get; }

    /// <summary>Table-level access: "readwrite" (default) or "readonly".</summary>
    public string Access { get; }

    /// <summary>Columns that override the table-level access to "readwrite".</summary>
    public string[]? ReadWriteColumns { get; set; }

    public SyncPermissionAttribute(SyncRole role, string access = "readwrite")
    {
        Role = role;
        Access = access;
    }
}
