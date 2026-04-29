namespace SqliteWasmBlazor.CryptoSync.Tests.Fixtures;

/// <summary>
/// In-memory test fixtures don't care about the deployment salt — they use
/// <see cref="RecordingWhitelistPushService"/> which never hashes pubkeys.
/// This shared constant supplies a deterministic placeholder so admin-side
/// invitation calls don't have to fabricate one each test. Live-relay tests
/// pass a real per-run salt directly.
/// </summary>
internal static class InvitationTestSalt
{
    /// <summary>32 bytes (0..31), Base64-encoded. Stable across test runs.</summary>
    public const string Default =
        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
}
