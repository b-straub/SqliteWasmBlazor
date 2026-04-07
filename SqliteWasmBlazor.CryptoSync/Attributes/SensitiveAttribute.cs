namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Marks an entity as sensitive — it lives only in the encrypted shadow table,
/// never in a plaintext open table. Sensitive entities are accessed exclusively
/// through <c>SensitiveAccessService</c> with single-record decrypt-on-demand.
///
/// PWA-safe: no plaintext at rest, ever. The OS may kill the app at any time
/// without warning, so any session-end purge of an open table would be unreliable.
/// By never writing the plaintext copy in the first place, the at-rest exposure
/// window is closed structurally.
///
/// Constraint: sensitive entities cannot participate in EF Core navigation properties
/// from non-sensitive entities, and are not queryable via SQL/joins.
/// Use only for small records (profiles, notes, secrets) accessed one at a time.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SensitiveAttribute : Attribute;
