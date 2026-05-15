using Microsoft.Extensions.Options;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Services;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

public class SecureKeyCacheStrategyTests
{
    [Fact]
    public void NoneStrategy_RemovesKeyAfterTryGet()
    {
        var cache = new SecureKeyCache(Options.Create(new KeyCacheOptions
        {
            Strategy = KeyCacheStrategy.NONE
        }));
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        cache.Store("k", key);
        var first = cache.TryGet("k");
        var second = cache.TryGet("k");

        Assert.NotNull(first);
        Assert.Null(second);
    }
}
