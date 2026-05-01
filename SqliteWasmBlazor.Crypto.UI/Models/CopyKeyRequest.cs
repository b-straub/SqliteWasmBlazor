namespace SqliteWasmBlazor.Crypto.UI.Models;

/// <summary>
/// Reactive copy-to-clipboard signal carried on a model property. The
/// component-trigger override reads <see cref="PublicKey"/> for the
/// clipboard write and <see cref="Label"/> for the snackbar message.
/// </summary>
public sealed record CopyKeyRequest(string PublicKey, string Label);
