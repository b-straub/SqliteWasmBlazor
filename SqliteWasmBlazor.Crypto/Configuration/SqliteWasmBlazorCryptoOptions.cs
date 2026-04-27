using SqliteWasmBlazor.Crypto.Extensions;
using SqliteWasmBlazor.Hosting;

namespace SqliteWasmBlazor.Crypto.Configuration;

/// <summary>
/// Configuration for SqliteWasmBlazor.Crypto JavaScript asset resolution. Registered via
/// <see cref="ServiceCollectionExtensions.AddSqliteWasmBlazorCrypto"/>.
/// </summary>
public sealed class SqliteWasmBlazorCryptoOptions : SqliteWasmAssetOptions
{
    public SqliteWasmBlazorCryptoOptions()
    {
        AssetRoot = "_content/SqliteWasmBlazor.Crypto/";
    }
}
