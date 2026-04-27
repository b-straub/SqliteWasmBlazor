namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// Domain-controlled "something happened" ping to one or more recipient
/// devices. Push is fundamentally one-way (sender → push gateway → receiver
/// OS) so this seam is <b>send-only</b>; the receiving device picks up the
/// notification through its OS push registration, not through this interface.
///
/// <para>
/// The payload is <b>byte-opaque</b> on purpose — the framework doesn't
/// know what a domain wants to ship (a messenger pushes
/// <c>{messageId, timestamp}</c> pointers; a presence app pushes a status
/// byte; a no-op consumer pushes nothing). The domain serializes its own
/// shape and hands the bytes here. Concrete impls
/// (WebPush/VAPID, declarative push, local toast, …) live outside the
/// base library or pluggable behind this interface.
/// </para>
///
/// <para>
/// Distinct from <see cref="ISyncTransport"/>: that interface ships
/// encrypted delta envelopes through a dumb relay; this one fires
/// <b>notification pings</b> through a push gateway. They're independent
/// transports for independent concerns — a typical wake-up flow uses both
/// (push fires a small pointer, recipient pulls the actual envelope via
/// <see cref="ISyncTransport"/>).
/// </para>
/// </summary>
public interface IPushNotifier
{
    /// <summary>
    /// Hand a notification payload to the push gateway for delivery to every
    /// recipient in <paramref name="recipientPublicKeys"/>. Returns once the
    /// gateway has accepted the request — <b>not</b> once recipients have
    /// been woken. Implementations should be cheap; long-running work
    /// belongs on a queue handed off here.
    /// </summary>
    ValueTask NotifyAsync(
        byte[] payload,
        IReadOnlyList<string> recipientPublicKeys,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op <see cref="IPushNotifier"/> for apps that don't ship push
/// notifications (or that wire push at the consumer layer only). Use
/// <see cref="Instance"/> to avoid allocations.
/// </summary>
public sealed class NullPushNotifier : IPushNotifier
{
    public static NullPushNotifier Instance { get; } = new();

    public ValueTask NotifyAsync(
        byte[] payload,
        IReadOnlyList<string> recipientPublicKeys,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
