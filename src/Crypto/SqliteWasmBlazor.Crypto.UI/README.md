# SqliteWasmBlazor.Crypto.UI — Plane 2 UI

The UI portion of **Plane 2: PRF-keyed at-rest encryption**. Plane 2 is
composed of two assemblies that ship together as one capability:

- `SqliteWasmBlazor.Crypto` — the engine (encrypted VFS, PRF key
  management, manifest, lifecycle). _Currently still colocated inside
  `SqliteWasmBlazor`; the plane-split pass will carve it out._
- `SqliteWasmBlazor.Crypto.UI` (this assembly) — the reference UI
  panels driving the engine.

Why two assemblies? So consumers who want a fully custom UI can replace
just the `.UI` nuget without forking the engine. The **`ObservableModel`
is the contract** — consumers can render their own markup against the
library's reactive models (Tier 2 customization, see below); the default
panels are reference implementations.

## What this assembly provides

- `AuthenticationModel` / `AuthenticationPanel` — register or sign in with
  a WebAuthn passkey; sole writer to `PrfAuthenticationStateProvider`.
- `EncryptionModel` / `EncryptionPage` — encrypted-VFS commands
  (Enter / Leave / Lock / Reset / Export-backup / Export-for-recipient /
  Import + plain-disk ZIP batch ops).
- `EncryptedDiskLifecycle` — auto-Unlock/Lock bridge tied to the
  authentication state cascade.
- `DbStateModel` — reactive boot status (replaces the event-bridged
  `DbInitializationService` from Plane 1 when this plane is active).
- `SessionExpiredPopover` / `RegistrationPanel` / `DatabaseErrorAlert` /
  `PublicKeyDisplay` — supporting panels.

The disk holds ChaCha20-Poly1305 slot-format ciphertext under a single
global key bound to a WebAuthn passkey via PRF. Plane 2 does **not** ship
sync — add Plane 3 (`SqliteWasmBlazor.CryptoSync.UI`) for that.

## Customization tiers

| Tier | What the consumer writes | What they ship |
|---|---|---|
| **0 — Drop-in** | `<EncryptionPage />` | Nothing |
| **1 — Slot tweaks** | `<EncryptionPage>` with `RenderFragment` template overrides | Tiny page wrapper |
| **2 — Custom panel** | `@inject EncryptionModel` + own markup against the model's reactive properties | A page in their app; library nugets unchanged |
| **3 — Replace this nuget** | Their own assembly with their own panels | Their nuget |

The Demo's `Pages/DatabaseEncryption.razor` is a Tier-2 example.

## Public registration

```csharp
// Program.cs
builder.Services.AddSqliteWasm();                    // Plane 1
builder.Services.AddSqliteWasmBlazorCrypto();        // Plane 2 engine
builder.Services.AddCryptoUI();                      // Plane 2 UI (this nuget)
builder.Services.AddCryptoUIPrfAuthenticator();      // WebAuthn-PRF auth seam

builder.Services.AddDbContextFactory<MyDbContext>(opt =>
    opt.UseSqliteWasm("Data Source=mydb.db"));

var host = builder.Build();
await host.Services.InitializeSqliteWasmDatabaseAsync<MyDbContext>(opt =>
{
    opt.BaseHref = builder.HostEnvironment.BaseAddress;
    opt.AssetRoot = "_content/SqliteWasmBlazor/";
});
```

Consumer pages wrap content in `<AuthorizeView Policy="DatabaseOpen">`.
The `NotAuthorized` branch should render `<AuthenticationPanel />`; the
authorized branch can freely use DbContexts.

## Host seams (optional)

| Interface | Default if not supplied | Purpose |
|---|---|---|
| `IPrfAuthenticator` | `AddCryptoUIPrfAuthenticator()` registers production | Wraps the PRF service register/derive ceremony |
| `IHostDatabaseService` | `NullHostDatabaseService.Instance` (no-op) | Full disk-wipe + remigrate orchestrator for Reset / boot-recovery |
| `ISessionAuthenticator` | host-supplied or panel-pinned | Re-auth gate for TTL-expired sessions |

Reset and session-expired flows operate on whichever seam the host
registers; absent registration leaves those flows as no-ops.

## Plane invariants (locked by build-time tests)

- This assembly never references `SqliteWasmBlazor.CryptoSync.*`.
- This assembly references the Plane 2 engine (`SqliteWasmBlazor.Crypto`
  once carved out; today still inside `SqliteWasmBlazor`); reverse
  direction is forbidden.
- Layer-leak guard: `ThreePlaneLayerGuardTests`.
