namespace SqliteWasmBlazor.Crypto.Abstractions.Models;

/// <summary>
/// Outcome of a push notification send attempt.
/// </summary>
/// <param name="Success">HTTP status was 2xx.</param>
/// <param name="Status">HTTP status code from the push service (0 on transport error).</param>
/// <param name="Endpoint">The push subscription endpoint URL.</param>
/// <param name="Gone">
/// True if the subscription was rejected as expired/unknown (404 / 410).
/// Caller should drop the subscription.
/// </param>
/// <param name="Reason">
/// Push service's <c>reason</c> field if it returned a structured error body.
/// Apple's canonical values include <c>VapidPkHashMismatch</c> (the JWT signing
/// key doesn't match the key the subscription was registered with) and
/// <c>BadJwtToken</c>. Null if the body wasn't structured JSON or the request
/// succeeded.
/// </param>
/// <param name="ResponseBody">Raw response body from the push service (best-effort, may be null).</param>
public sealed record PushSendResult(
    bool Success,
    int Status,
    string? Endpoint,
    bool Gone,
    string? Reason,
    string? ResponseBody)
{
    /// <summary>
    /// Convenience: the push service rejected the JWT because its signing key doesn't match
    /// the public key the subscription was registered with. Indicates the local copy of the
    /// recipient's VAPID identity is stale and the contact must rebroadcast a fresh one.
    /// </summary>
    public bool IsVapidKeyStale => Reason is "VapidPkHashMismatch";

    /// <summary>Transport error or VAPID key not loaded.</summary>
    public static PushSendResult Failure(string? endpoint = null) =>
        new(Success: false, Status: 0, Endpoint: endpoint, Gone: false, Reason: null, ResponseBody: null);
}
