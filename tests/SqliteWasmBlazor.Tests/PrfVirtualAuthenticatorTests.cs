using Microsoft.Playwright;
using SqliteWasmBlazor.Tests.Infrastructure;
using Xunit.Abstractions;

namespace SqliteWasmBlazor.Tests;

[CollectionDefinition("PrfWebAuthn", DisableParallelization = true)]
public class PrfWebAuthnCollection : ICollectionFixture<PrfWaFixture>
{
}

[Collection("PrfWebAuthn")]
public class PrfVirtualAuthenticatorTests(PrfWaFixture fixture, ITestOutputHelper output)
{
    private const string PrfTestPath = "/prf-vfs-test";

    // Dev-friendly waits: keep the first-button-visible wait long because it
    // covers a cold WASM boot, but compress everything afterwards so a stuck
    // step trips quickly during iteration.
    private const float FirstButtonVisibleTimeoutMs = 60000;
    private const float ButtonEnabledTimeoutMs = 10000;
    private const float StatusTimeoutMs = 8000;

    // Mirrors KeyCacheOptions.TtlMs configured in TestApp.Program.cs. Tests
    // that drive session expiry must wait > this, then receive "PRF session
    // ended" within StatusTimeoutMs.
    private const int SessionTtlMs = 5000;

    [Fact]
    [Trait("Browser", "Chromium")]
    public async Task RegistrationHappyPath_StoresCredential()
    {
        await using var scenario = await fixture.CreateScenarioAsync(output);

        await scenario.NavigateAsync(PrfTestPath);

        // First click absorbs the cold WASM boot; subsequent clicks fall back
        // to the dev-friendly ButtonEnabledTimeoutMs.
        await ClickAsync(scenario, "Register passkey", FirstButtonVisibleTimeoutMs);
        await ExpectStatusContainsAsync(scenario, "Passkey registered");

        var credentials = await GetCredentialsAsync(scenario, scenario.PrimaryAuthenticatorId);
        Assert.True(credentials.GetArrayLength() == 1,
            $"Expected 1 credential after registration, found {credentials.GetArrayLength()}.");

        var credentialId = credentials[0].GetProperty("credentialId").GetString();
        Assert.False(string.IsNullOrEmpty(credentialId), "Virtual authenticator returned an empty credential id.");
    }

    [Fact]
    [Trait("Browser", "Chromium")]
    public async Task CachedCredential_RoundTripsThroughEncryptedVfs()
    {
        await using var scenario = await fixture.CreateScenarioAsync(output);

        await scenario.NavigateAsync(PrfTestPath);

        // First click absorbs the cold WASM boot; subsequent clicks fall back
        // to the dev-friendly ButtonEnabledTimeoutMs.
        await ClickAsync(scenario, "Register passkey", FirstButtonVisibleTimeoutMs);
        await ExpectStatusContainsAsync(scenario, "Passkey registered");

        await ClickAsync(scenario, "Authenticate and open");
        // Current UI separates authentication/key install from the first
        // write; Insert materialises the encrypted DB after auth succeeds.
        await ExpectStatusContainsAsync(scenario, "Authenticated.");

        await ClickAsync(scenario, "Insert + read 25 rows");
        await ExpectStatusContainsAsync(scenario, "Round trip OK — total rows: 25");

        await ClickAsync(scenario, "Read row count (no writes)");
        await ExpectStatusContainsAsync(scenario, "Row count: 25");
    }

    [Fact]
    [Trait("Browser", "Chromium")]
    public async Task CredentialMismatch_SurfacesWrongKey()
    {
        await using var scenario = await fixture.CreateScenarioAsync(output);

        await scenario.NavigateAsync(PrfTestPath);

        // Stage 1 — Register A → boot a fresh DB encrypted under A's pubkey-bytes.
        await ClickAsync(scenario, "Register passkey", FirstButtonVisibleTimeoutMs);
        await ExpectStatusContainsAsync(scenario, "Passkey registered");

        // Capture A's credentialId before B exists so we can target it for
        // removal in Stage 2.
        var afterA = await GetCredentialsAsync(scenario, scenario.PrimaryAuthenticatorId);
        Assert.True(afterA.GetArrayLength() == 1,
            $"Expected 1 credential after registering A, found {afterA.GetArrayLength()}.");
        var credentialA = afterA[0].GetProperty("credentialId").GetString()
            ?? throw new InvalidOperationException("Credential A returned a null credentialId.");

        await ClickAsync(scenario, "Authenticate and open");
        await ExpectStatusContainsAsync(scenario, "Authenticated.");
        await ClickAsync(scenario, "Insert + read 25 rows");
        await ExpectStatusContainsAsync(scenario, "Round trip OK — total rows: 25");
        await ClickAsync(scenario, "Lock (close DB + drop PRF session)");
        await ExpectStatusContainsAsync(scenario, "DB closed");

        // Stage 2 — Register a second credential B on the SAME authenticator.
        // Matches the page's documented "register a second passkey" workflow.
        // Pick A's credential off the authenticator afterwards so the
        // discoverable PRF ceremony has only B to answer with — avoids
        // depending on Chrome's auto-presence credential ordering, which is
        // undefined when multiple resident credentials live on one virtual
        // authenticator.
        await ClickAsync(scenario, "Register passkey");
        await ExpectStatusContainsAsync(scenario, "Passkey registered");

        var bothCreds = await GetCredentialsAsync(scenario, scenario.PrimaryAuthenticatorId);
        Assert.True(bothCreds.GetArrayLength() == 2,
            $"Expected 2 credentials after registering B, found {bothCreds.GetArrayLength()}.");

        await scenario.SendCdpAsync("WebAuthn.removeCredential", new Dictionary<string, object>
        {
            ["authenticatorId"] = scenario.PrimaryAuthenticatorId,
            ["credentialId"] = credentialA,
        });

        // Stage 3 — only B can answer; the disk's manifest is owned by A.
        // The page's wrong-passkey early-out reads the manifest credentialId
        // via Session.GetStateAsync() and refuses BEFORE installing the
        // wrong-fit globalKey, so the failure surfaces at the auth step
        // (clean message) instead of as SQLITE_IOERR on the first read.
        await ClickAsync(scenario, "Authenticate and open");
        await ExpectStatusContainsAsync(scenario, "Wrong passkey for this DB");
    }

    [Fact]
    [Trait("Browser", "Chromium")]
    public async Task RekeyCeremony_PreservesRowsUnderNewKey()
    {
        await using var scenario = await fixture.CreateScenarioAsync(output);

        await scenario.NavigateAsync(PrfTestPath);

        // First click absorbs the cold WASM boot; subsequent clicks fall back
        // to the dev-friendly ButtonEnabledTimeoutMs.
        await ClickAsync(scenario, "Register passkey", FirstButtonVisibleTimeoutMs);
        await ExpectStatusContainsAsync(scenario, "Passkey registered");

        await ClickAsync(scenario, "Authenticate and open");
        await ExpectStatusContainsAsync(scenario, "Authenticated.");

        await ClickAsync(scenario, "Insert + read 25 rows");
        await ExpectStatusContainsAsync(scenario, "Round trip OK — total rows: 25");

        // Auto-extracted armored pubkey appears in the readonly textarea once
        // the page has the active passkey's PRF session cached.
        var readonlyArea = scenario.Page.Locator("textarea[readonly]");
        await Assertions.Expect(readonlyArea).ToBeVisibleAsync(new() { Timeout = 10000 });
        var armoredPubkey = await readonlyArea.InputValueAsync();
        Assert.Contains("BEGIN PFA PUBLIC KEY", armoredPubkey);

        // Same-passkey rekey: paste the active passkey's own armored pubkey as
        // the rotate target. Exercises ExportDatabaseAsync(REKEY) → wipe →
        // re-install → ImportDatabaseAsync verify-on-write end-to-end. After
        // auto-lock, reopening with the same passkey must install the global
        // key, and the following read proves the rotated rows survived.
        var targetField = scenario.Page.GetByLabel("Target passkey pubkey (PFA armor)");
        await targetField.FillAsync(armoredPubkey);
        // MudTextField @bind-Value commits on blur (default Immediate=false),
        // so FillAsync alone leaves the C# state empty — Tab forces the blur
        // event that flushes _pastedArmoredPubkey and re-enables the Rotate
        // button.
        await scenario.Page.Keyboard.PressAsync("Tab");

        await ClickAsync(scenario, "Rotate to pasted pubkey (auto-locks)");
        await ExpectStatusContainsAsync(scenario, "Rotated — local DB now encrypted");

        await ClickAsync(scenario, "Authenticate and open");
        await ExpectStatusContainsAsync(scenario, "Authenticated.");

        await ClickAsync(scenario, "Read row count (no writes)");
        await ExpectStatusContainsAsync(scenario, "Row count: 25");
    }

    [Fact]
    [Trait("Browser", "Chromium")]
    public async Task SessionExpiresOnTtl_DropsKeyAndReEnablesAuth()
    {
        await using var scenario = await fixture.CreateScenarioAsync(output);

        await scenario.NavigateAsync(PrfTestPath);

        // First click absorbs the cold WASM boot; subsequent clicks fall back
        // to the dev-friendly ButtonEnabledTimeoutMs.
        await ClickAsync(scenario, "Register passkey", FirstButtonVisibleTimeoutMs);
        await ExpectStatusContainsAsync(scenario, "Passkey registered");

        await ClickAsync(scenario, "Authenticate and open");
        await ExpectStatusContainsAsync(scenario, "Authenticated.");

        // Authenticate buttons stay disabled while a PRF session is active
        // (Disabled bindings on PrfService.HasCachedKeys()). Confirm the
        // pre-expiry state before the timer fires.
        var authButton = scenario.Page.GetByRole(AriaRole.Button,
            new() { Name = "Authenticate and open", Exact = true });
        await Assertions.Expect(authButton).ToBeDisabledAsync(
            new() { Timeout = ButtonEnabledTimeoutMs });

        // Wait past the configured TTL. SecureKeyCache + JS-side key cache
        // both fire after SessionTtlMs; KeyExpired observable fans out to
        // PrfVfsTest.OnKeyExpired which clears _keyInstalled / _armoredOwnPubkey
        // and posts the "PRF session ended" alert.
        await ExpectStatusContainsAsync(
            scenario,
            "PRF session ended",
            timeoutMs: SessionTtlMs + StatusTimeoutMs);

        // After the timer fires HasCachedKeys() returns false, so the
        // Authenticate button must re-enable — the timer/observable wire-up
        // is the path under test (Lock + KeyExpired-fires-UI-update is
        // already covered by scenario 3).
        await Assertions.Expect(authButton).ToBeEnabledAsync(
            new() { Timeout = StatusTimeoutMs });
    }

    private static async Task ClickAsync(PrfScenario scenario, string buttonName, float? timeoutMs = null)
    {
        var button = scenario.Page.GetByRole(AriaRole.Button, new() { Name = buttonName, Exact = true });
        await Assertions.Expect(button).ToBeEnabledAsync(new() { Timeout = timeoutMs ?? ButtonEnabledTimeoutMs });
        await button.ClickAsync();
    }

    private static Task ExpectStatusContainsAsync(PrfScenario scenario, string substring, float? timeoutMs = null)
    {
        var alert = scenario.Page.Locator(".mud-alert").Last;
        return Assertions.Expect(alert).ToContainTextAsync(substring, new() { Timeout = timeoutMs ?? StatusTimeoutMs });
    }

    private static async Task<System.Text.Json.JsonElement> GetCredentialsAsync(PrfScenario scenario, string authenticatorId)
    {
        var response = await scenario.SendCdpAsync(
            "WebAuthn.getCredentials",
            new Dictionary<string, object>
            {
                ["authenticatorId"] = authenticatorId,
            });
        Assert.NotNull(response);
        return response.Value.GetProperty("credentials");
    }
}
