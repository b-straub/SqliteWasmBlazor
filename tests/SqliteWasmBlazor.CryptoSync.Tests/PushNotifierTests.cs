using SqliteWasmBlazor.CryptoSync.Tests.Fixtures;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Contract coverage for <see cref="IPushNotifier"/> — the byte-opaque,
/// recipient-pubkey-addressed notification seam. Concrete impls
/// (WebPush/VAPID, declarative push, local toast) live outside the base
/// library; these tests stay at the seam.
/// </summary>
public class PushNotifierTests
{
    [Fact]
    public void NullPushNotifier_Instance_IsSingleton()
    {
        Assert.Same(NullPushNotifier.Instance, NullPushNotifier.Instance);
    }

    [Fact]
    public async Task NullPushNotifier_NotifyAsync_CompletesWithoutThrowing()
    {
        await NullPushNotifier.Instance.NotifyAsync(
            payload: [0x01, 0x02],
            recipientPublicKeys: ["bob-pub"]);
    }

    [Fact]
    public async Task RecordingPushNotifier_CapturesPayloadAndRecipients()
    {
        var notifier = new RecordingPushNotifier();

        await notifier.NotifyAsync([0xAA, 0xBB], ["bob-pub", "carol-pub"]);

        var call = Assert.Single(notifier.Calls);
        Assert.Equal([0xAA, 0xBB], call.Payload);
        Assert.Equal(["bob-pub", "carol-pub"], call.RecipientPublicKeys);
    }

    [Fact]
    public async Task RecordingPushNotifier_PreservesCallOrder()
    {
        var notifier = new RecordingPushNotifier();

        await notifier.NotifyAsync([0x01], ["bob-pub"]);
        await notifier.NotifyAsync([0x02], ["carol-pub"]);
        await notifier.NotifyAsync([0x03], ["dave-pub"]);

        var calls = notifier.Calls.ToArray();
        Assert.Equal(3, calls.Length);
        Assert.Equal([0x01], calls[0].Payload);
        Assert.Equal([0x02], calls[1].Payload);
        Assert.Equal([0x03], calls[2].Payload);
    }
}
