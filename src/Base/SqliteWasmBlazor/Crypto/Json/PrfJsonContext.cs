using System.Text.Json;
using System.Text.Json.Serialization;
using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.Json;

/// <summary>
/// Source-generated JSON serialization context for PRF types.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PrfCredential))]
[JsonSerializable(typeof(DiscoverablePrfOutput))]
[JsonSerializable(typeof(AsymmetricEncryptedData))]
[JsonSerializable(typeof(SymmetricEncryptedData))]
[JsonSerializable(typeof(PrfResult<PrfCredential>))]
[JsonSerializable(typeof(PrfResult<DiscoverablePrfOutput>))]
[JsonSerializable(typeof(PrfResult<AsymmetricEncryptedData>))]
[JsonSerializable(typeof(PrfResult<string>))]
[JsonSerializable(typeof(JsPrfOptions))]
public partial class PrfJsonContext : JsonSerializerContext;

/// <summary>
/// PRF options as expected by JavaScript.
/// </summary>
public sealed record JsPrfOptions(
    string RpName,
    string? RpId,
    int TimeoutMs,
    string AuthenticatorAttachment
);
