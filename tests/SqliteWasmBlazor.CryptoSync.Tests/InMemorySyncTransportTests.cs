using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Behaviour coverage for <see cref="InMemorySyncTransport"/> /
/// <see cref="InMemorySyncRelay"/>. The pair stand in for a real
/// whitelist-broadcast relay in xUnit scenarios — single FIFO queue,
/// every receiver drains from the same stream, payload-level addressing is
/// the receiver's crypto layer's job.
/// </summary>
public class InMemorySyncTransportTests
{
    [Fact]
    public async Task SendAsync_DeliversToEveryReceiver()
    {
        var relay = new InMemorySyncRelay();
        var aliceTx = new InMemorySyncTransport(relay);
        var bobTx = new InMemorySyncTransport(relay);
        var carolTx = new InMemorySyncTransport(relay);

        await aliceTx.SendAsync([0x01]);

        // Broadcast: any receiver drains the queue. With one envelope total,
        // exactly one TryReceive returns bytes; the rest see an empty queue.
        var first = await bobTx.TryReceiveAsync();
        var second = await carolTx.TryReceiveAsync();

        Assert.Equal([0x01], first!);
        Assert.Null(second);
    }

    [Fact]
    public async Task SendAsync_FifoAcrossReceivers()
    {
        var relay = new InMemorySyncRelay();
        var aliceTx = new InMemorySyncTransport(relay);
        var bobTx = new InMemorySyncTransport(relay);

        await aliceTx.SendAsync([0x01]);
        await aliceTx.SendAsync([0x02]);
        await aliceTx.SendAsync([0x03]);

        Assert.Equal([0x01], (await bobTx.TryReceiveAsync())!);
        Assert.Equal([0x02], (await bobTx.TryReceiveAsync())!);
        Assert.Equal([0x03], (await bobTx.TryReceiveAsync())!);
        Assert.Null(await bobTx.TryReceiveAsync());
    }

    [Fact]
    public async Task TamperHook_AppliesBeforeFanOut()
    {
        var relay = new InMemorySyncRelay
        {
            Tamper = bytes => [.. bytes.Select(b => (byte)(b ^ 0xFF))]
        };
        var aliceTx = new InMemorySyncTransport(relay);
        var bobTx = new InMemorySyncTransport(relay);

        await aliceTx.SendAsync([0x00, 0x01, 0x02]);

        Assert.Equal([0xFF, 0xFE, 0xFD], (await bobTx.TryReceiveAsync())!);
    }

    [Fact]
    public async Task SendAsync_ClonesEnvelope_NoCrossContamination()
    {
        var relay = new InMemorySyncRelay();
        var aliceTx = new InMemorySyncTransport(relay);
        var bobTx = new InMemorySyncTransport(relay);

        var source = new byte[] { 0x10, 0x20, 0x30 };
        await aliceTx.SendAsync(source);
        source[0] = 0xFF;

        var bobBytes = (await bobTx.TryReceiveAsync())!;
        Assert.Equal([0x10, 0x20, 0x30], bobBytes);
    }

    [Fact]
    public async Task PendingCount_TracksQueue()
    {
        var relay = new InMemorySyncRelay();
        var aliceTx = new InMemorySyncTransport(relay);
        var bobTx = new InMemorySyncTransport(relay);

        Assert.Equal(0, relay.PendingCount);
        await aliceTx.SendAsync([0x01]);
        await aliceTx.SendAsync([0x02]);
        Assert.Equal(2, relay.PendingCount);

        await bobTx.TryReceiveAsync();
        Assert.Equal(1, relay.PendingCount);
    }
}
