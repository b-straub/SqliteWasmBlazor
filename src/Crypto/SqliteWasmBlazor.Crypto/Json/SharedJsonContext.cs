using System.Text.Json;
using System.Text.Json.Serialization;
using SqliteWasmBlazor.Crypto.Abstractions.Models;

namespace SqliteWasmBlazor.Crypto.Abstractions.Json;

/// <summary>
/// Source-generated JSON serialization context for shared PRF types.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AsymmetricEncryptedData))]
[JsonSerializable(typeof(SymmetricEncryptedData))]
[JsonSerializable(typeof(PrfResult<AsymmetricEncryptedData>))]
[JsonSerializable(typeof(PrfResult<SymmetricEncryptedData>))]
[JsonSerializable(typeof(PrfResult<string>))]
public partial class SharedJsonContext : JsonSerializerContext;
