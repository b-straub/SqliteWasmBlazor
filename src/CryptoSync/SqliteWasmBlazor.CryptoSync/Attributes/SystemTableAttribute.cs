namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Marks an entity as a system table managed by the admin (instance creator).
/// System tables are seeded by the generator into <c>SystemTableRegistry</c> and are
/// the refusal source for ownership transfer — their scope ownership cannot be moved
/// off the admin device.
///
/// Examples: <c>TrustedContact</c>, <c>SentInvitation</c>, <c>ReceivedInvitation</c>,
/// <c>SharingKey</c>, <c>SyncPermission</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SystemTableAttribute : Attribute;
