namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Defines CRUD permissions for a role on the entity's table.
/// Default = full access for all roles. Override individual operations.
/// Applied to SyncableEntity classes. Generator produces EF Core HasData() seed calls.
///
/// Example:
/// [Permissions("Editor", Delete = "Owner")]  // Editor can CRUD, only Owner can delete
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class PermissionsAttribute(string defaultRole = PermissionsAttribute.Any) : Attribute
{
    /// <summary>Role that can Create rows. Default = Any (all roles).</summary>
    public string Create { get; set; } = defaultRole;

    /// <summary>Role that can Read rows. Default = Any (all roles).</summary>
    public string Read { get; set; } = defaultRole;

    /// <summary>Role that can Update rows. Default = Any (all roles).</summary>
    public string Update { get; set; } = defaultRole;

    /// <summary>Role that can Delete rows. Default = Any (all roles).</summary>
    public string Delete { get; set; } = defaultRole;

    /// <summary>Wildcard: all roles have this permission.</summary>
    public const string Any = "Any";
}

/// <summary>
/// Defines which role can share to which target role.
/// Applied to SyncableEntity classes.
///
/// Example:
/// [Share("Owner", "Editor")]   // Owner can share as Editor
/// [Share("Owner", "Viewer")]   // Owner can share as Viewer
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ShareAttribute(string fromRole, string toRole) : Attribute
{
    public string FromRole { get; } = fromRole;
    public string ToRole { get; } = toRole;
}

/// <summary>
/// Marks a property as the display label when sharing (e.g. list name).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ShareLabelAttribute : Attribute;

/// <summary>
/// Allows specific roles to update this column (overrides table-level restriction).
/// Applied to properties on SyncableEntity classes.
///
/// Example:
/// [AllowUpdate("Viewer")]  // Viewer CAN update this column
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AllowUpdateAttribute(string roles) : Attribute
{
    public string Roles { get; } = roles;
}

/// <summary>
/// Denies specific roles from updating this column.
/// Applied to properties on SyncableEntity classes.
///
/// Example:
/// [DenyUpdate("Viewer")]  // Viewer CANNOT update this column
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DenyUpdateAttribute(string roles) : Attribute
{
    public string Roles { get; } = roles;
}

/// <summary>
/// Inherits permissions from a parent entity's table.
/// Child entity uses parent's CRUD permissions. Useful for FK relationships.
///
/// Example:
/// [InheritPermissions("ShoppingLists")]  // ShoppingItem inherits ShoppingList permissions
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class InheritPermissionsAttribute(string table) : Attribute
{
    public string Table { get; } = table;
}
