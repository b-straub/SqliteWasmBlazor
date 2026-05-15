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
        // Plane-split invariant: a plane never ships plane N+1 assets.
        // crypto-bridge.js is plane-2-only, served from this package's _content.
        AssetRoot = "_content/SqliteWasmBlazor.Crypto/";
    }
}
