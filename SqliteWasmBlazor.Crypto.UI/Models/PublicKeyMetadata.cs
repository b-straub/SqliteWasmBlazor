namespace SqliteWasmBlazor.Crypto.UI.Models;

/// <summary>
/// Optional human-facing labels for a public key shown in the UI. Carried
/// alongside the key in <see cref="Components.Authentication.PublicKeyDisplay"/>
/// so the user can attach a name / email / comment for handoff. The fields
/// are pure UI metadata — they do not participate in any wire-level signing
/// or encryption.
/// </summary>
public sealed class PublicKeyMetadata
{
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? Comment { get; init; }
    public DateOnly? Created { get; init; }
}
