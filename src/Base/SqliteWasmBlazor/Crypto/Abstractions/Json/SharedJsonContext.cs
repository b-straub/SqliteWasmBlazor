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
[JsonSerializable(typeof(SignedEnvelope))]
[JsonSerializable(typeof(SymmetricEncryptedData))]
[JsonSerializable(typeof(SignedData))]
[JsonSerializable(typeof(KeyPair))]
[JsonSerializable(typeof(PrfResult<AsymmetricEncryptedData>))]
[JsonSerializable(typeof(PrfResult<SymmetricEncryptedData>))]
[JsonSerializable(typeof(PrfResult<SignedData>))]
[JsonSerializable(typeof(PrfResult<string>))]
[JsonSerializable(typeof(PrfResult<KeyPair>))]
public partial class SharedJsonContext : JsonSerializerContext;

/// <summary>
/// Source-generated JSON serialization context for SignedData.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SignedData))]
public partial class SignedDataJsonContext : JsonSerializerContext;
