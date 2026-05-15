# SqliteWasmBlazor.CryptoSync.UI — Plane 3 UI

The UI portion of **Plane 3: encrypted multi-device sync**. Plane 3 is
composed of two assemblies that ship together as one capability:

- `SqliteWasmBlazor.CryptoSync` — the engine (delta envelope, group keys,
  relay transport, EF interceptors, generator-emitted shadow tables,
  domain entities).
- `SqliteWasmBlazor.CryptoSync.UI` (this assembly) — the reference UI
  panels for contacts / invitations / groups / sync state.

Why two assemblies? The engine has no Blazor dependency, so non-Blazor
hosts (e.g. a console relay-side tool) can consume the sync stack
without a UI framework. The UI assembly adds the panels for Blazor
consumers; consumers who want a fully custom UI can replace just this
nuget without forking the engine. The **`ObservableModel` is the
contract** — see the customization tiers below.

## What this assembly provides

- RxBlazor models: `ContactsModel`, `InvitationModel`, `UserProfileModel`,
  plus a per-plane override of `AuthenticationModel` / `RegistrationModel`
  (Plane 3 layers identity flows on top of Plane 2's PRF auth).
- Panels: `ContactsPanel`, `InvitationPanel`, `UserProfilePanel`,
  `PushPanel`, plus re-exports of Plane 2's auth + error panels.
- `IAdminInvitationContext` — host-supplied admin context for invitation
  create/ingest flows.

The non-UI `SqliteWasmBlazor.CryptoSync` library hosts:

- `CryptoSyncContextBase` — your `DbContext` inherits this.
- `SyncEngine` / `SyncOrchestrator` — encrypted delta export / import / key
  rotation via the worker's crypto path.
- `GroupService` / `ContactService` / `ContactInvitationService` /
  `DeviceIdentityService` / `LeaveService` / `TransferService` /
  `GroupTransferService` / `SharingService` — sync domain services.
- `IWhitelistPushService` / `IAdminPinService` — admin-only relay clients.
- `HttpSyncTransport` + `ISyncTransport` — pluggable network transport.
- `SqliteWasmBlazor.CryptoSync.Generator` — emits shadow tables + sync
  registries from `[Permission]`-annotated domain types.

## Customization tiers

| Tier | What the consumer writes | What they ship |
|---|---|---|
| **0 — Drop-in** | `<ContactsPanel />` / `<InvitationPanel />` etc. | Nothing |
| **1 — Slot tweaks** | Panels with `RenderFragment` template overrides | Tiny page wrappers |
| **2 — Custom panel** | `@inject ContactsModel` + own markup against the model's reactive properties | Pages in their app |
| **3 — Replace this nuget** | Their own assembly with their own panels | Their nuget |

## Public registration

```csharp
// Program.cs — full plane-3 stack
builder.Services.AddSqliteWasm();                    // Plane 1
builder.Services.AddSqliteWasmBlazorCrypto();        // Plane 2 engine
builder.Services.AddCryptoUI();                      // Plane 2 UI
builder.Services.AddCryptoUIPrfAuthenticator();
builder.Services.AddCryptoSync<MyDbContext>(opt =>   // Plane 3 engine
{
    opt.RelayBaseUri = new Uri("https://your-relay/");
    opt.DeploymentSaltBase64 = "...";
});
builder.Services.AddCryptoSyncUI();                  // Plane 3 UI (this nuget)
builder.Services.AddCryptoSyncPrfSigners();          // PRF-backed relay auth

builder.Services.AddDbContextFactory<MyDbContext>(opt =>
    opt.UseSqliteWasm("Data Source=mydb.db"));
```

`MyDbContext` must inherit from `CryptoSyncContextBase` and apply the
generator (`SqliteWasmBlazor.CryptoSync.Generator`); the generator
emits shadow tables + sync registries from the `[Permission]`-annotated
domain types.

## Host seams (optional)

| Interface | Default if not supplied | Purpose |
|---|---|---|
| `IAdminInvitationContext` | host/page-supplied (admin-only) | Resolves admin key material + deployment salt + transport for invitation create/ingest |
| `ISenderAuthSigner` / `IReceiveAuthSigner` | `AddCryptoSyncPrfSigners()` registers PRF-backed defaults | Relay POST/GET challenge signers |
| `IReceiveCursorStore` | `EfReceiveCursorStore` (auto-fallback to `InMemoryReceiveCursorStore`) | Persists per-device receive cursor |

## Plane invariants (locked by build-time tests)

- The non-UI `SqliteWasmBlazor.CryptoSync` assembly never references its
  own UI layer `SqliteWasmBlazor.CryptoSync.UI` — non-Blazor hosts can
  consume the sync engine without a Blazor dep.
- Layer-leak guard: `ThreePlaneLayerGuardTests`.
