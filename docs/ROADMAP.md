# SqliteWasmBlazor Roadmap

_Single source of truth for "where are we." Last updated 2026-05-01 against branch `crypto-sync` HEAD `1a62974` (Phase 1 of plane-separation + test buildup is fully shipped; **Phase 2 R1 is complete** — all five deterministic-unit slots green: KDF byte-equality vectors, canonical envelope agreement, SigningService + AsymmetricEncryptionService keyId-cache round-trips, JS bridge dispatch. **Pre-R2 interop sweep is also complete** (`1a62974`, on top of the `0fa6708` bundle refresh) — three new cross-language vector files: (a) `CrossLanguageWrappingKeyVectorTests` / `crossLanguageWrappingKey.test.ts` pin `DeriveWrappingKey` (X25519 ECDH + HKDF) byte-for-byte across three contexts, (b) `CrossLanguageAesGcmVectorTests` / `crossLanguageAesGcm.test.ts` pin AES-256-GCM at fixed nonce (the underlying primitive of `WrapContentKey`/`UnwrapContentKey`) across zeros/ones/sequential keys, with-and-without AAD, (c) `CrossLanguageEciesVectorTests` / `crossLanguageEcies.test.ts` pin the full ECIES combinator (X25519 ECDH + HKDF "ecies-aes-gcm" + AES-256-GCM at fixed nonce) and verify production decrypt round-trips the captured ciphertexts. Push payload framing was scoped out — `encryptPushPayload` exists only on the TS side, no C# counterpart for cross-language equality. Plus follow-up cleanups: `8bc0af2` dropped the orphaned `crypto-core/dist/` tsc build (npm publishing is out of scope, esbuild + vitest read TS source via the package `main` field), and `2c716ca` flattened the `TypeScript-Crypto/packages/crypto-core/` workspace into a single package — primitives now live at `TypeScript-Crypto/src/crypto-core/`, tests at `TypeScript-Crypto/tests/`. The R1.2 vector exposed and resolved a real C#↔TS interop bug — `GroupEncryptionService.BuildCanonicalEnvelope` was hashing the UTF-8 bytes of the base64 ciphertext string while the TS `buildCanonicalEnvelope` hashed raw bytes; commit `7d77f00` fixes the C# side to decode-then-hash so envelopes agree byte-for-byte. The pre-R2 sweep itself was clean — every new vector landed green on first run, no further interop bugs surfaced. **Next up:** Phase 2 R2 — virtual-authenticator E2E. Pin Playwright virtual-authenticator API version, build `WaFixtureBase` with `IPage.Context.AddVirtualAuthenticatorAsync`, then four PRF scenarios (register, unlock+open, mismatch, rekey). Stage A (whitelist-broadcast rewrite) is fully complete and the Codex memory-hygiene audit close-out (zeroization + binary boundary + non-extractable JS keys + second-pass HKDF/seed/packed-buffer cleanup + third-pass B64-result-buffer clears + storeKeys failure-path scoping + legacy single-key API removal) is in. **Stage 2 (UI absorption) panels are all ✅ except slot 10 (xUnit bootstrap), which folds into Phase 2 of the active workstream — `plane-separation-and-test-buildup.md` is the active plan.** The Post-Stage-2 sequence (WebAuthn demo / TestApp / admin-seeding) sequences on top of the plane-separated structure rather than on Stage 2's monolithic UI library. Stage B (production PRF/WebAuthn signer wiring) is folded into Phase 3 of the new plan — it remains not a standalone item._

This document supersedes the multiple parallel numbering systems (Stage / Phase A / Phase B / Audit Phase 1-3) that were used while individual workstreams were in flight. Going forward, work is grouped only as **Active**, **Postponed**, **Done**, **Deferred**.

If you're picking this up cold, read in this order:
1. This document (5 min) — bird's-eye view.
2. `docs/security/threat-model.md` (5 min) — policy stance that locks several catalog verdicts.
3. `docs/security/property-catalog.md` (10 min) — 18-property security audit; 17/18 covered, 1 deferred.
4. The plan file under **Active** below (10 min) — the actual next-step script.

---

## Active

### Stage 2 — UI absorption (`SqliteWasmBlazor.CryptoSync.UI`)

- **Plan:** `~/.claude/plans/cryptosync-ui-absorption.md` (written 2026-04-29).
- **Trigger:** fired by Stage A completion. Stage A's wire stack is locked, so the panels can be authored against stable contracts.
- **Estimated effort:** 1-2 weeks (depends on how much rescaffolding is wanted vs. straight ports).
- **RxBlazorV2 is mandatory** for the new library — every panel ships as three files (`*.razor` markup-only, `*.razor.cs` host-glue partial, `*Model.cs` `ObservableModel`). Re-skinning is a markup-only refactor by construction.
- **Hosts consuming `CryptoSync.UI` must themselves be RxBlazor-based** — a non-RxBlazor host on top of reactive panels would mix two notification paradigms. Library extension points are therefore expressed as either (a) host-supplied service seams (`IPrfAuthenticator`, `IAdminInvitationContext`, `IDatabaseResetService`, `ISessionAuthenticator`) or (b) observable model properties hosts watch via internal/external observers — never `[Parameter] EventCallback` bubbles.

**Slot status (build order from the plan):**

| # | Slot | Status |
|---|---|---|
| 1 | Project scaffold + `_Imports.razor` + empty `AddCryptoSyncUI()` | ✅ DONE — `SqliteWasmBlazor.CryptoSync.UI` builds against `RxBlazorV2.MudBlazor` 1.2.2 + `SqliteWasmBlazor` + `.CryptoSync`; registered in `SqliteWasmBlazor.slnx`. |
| 2 | `DatabaseErrorAlert` (Shared) | ✅ DONE — boot-status switch over `IDbInitFailure`; reset is a model command bound to a host-supplied `IDatabaseResetService` seam (`NullDatabaseResetService.Instance` for hosts without recovery). Component bridges the non-reactive `IDbInitializationStatus.Changed` event into `Model.Failure`. |
| 3 | `PublicKeyDisplay` (Shared) | ✅ DONE — folded into `AuthenticationPanel` (RXBG061 forbids same-assembly composition of `*ModelComponent` panels). All key + metadata + dialog state lives on `AuthenticationModel`; clipboard is a component-trigger over `Model.PendingCopy`. Promotable to a downstream-consumer panel if a second use-case appears. |
| 4 | `SessionExpiredPopover` (Shared) | ✅ DONE — `Visible` is a model property (host writes `true` to show); `ReAuthenticate`/`Dismiss` commands delegate to a host-supplied `ISessionAuthenticator` seam and clear `Visible` on completion. |
| 5 | `UserProfilePanel` | ✅ DONE — read-only render of `DeviceSettings` (DeviceName, ClientGuid, IsAdmin, AdminContactId, OwnContactId, CredentialId hint) via `UserProfileModel`/`DeviceIdentityService`. |
| 6 | `AuthenticationPanel` + `RegistrationPanel` | ✅ DONE — both bound to a host-supplied `IPrfAuthenticator` seam (declared in `Services/IPrfAuthenticator.cs`). `AuthenticationModel` also owns the embedded public-key display + metadata-edit dialog state (per slot 3). RxBlazor hosts react via observers on `Model.CredentialId` / `Model.PublicKey` / `Model.Metadata` — no EventCallback bridges. Production PRF impl lands in the post-Stage-2 demo step. |
| 7 | `ContactsPanel` | ✅ DONE — read + per-row local soft-delete + copy-pubkey via `ContactService`. Copy is a `RequestCopyKey` command + `PendingCopy` component-trigger (model owns the intent, component does JS+snackbar). End-to-end admin revoke (rotation + whitelist push) requires `DualKeyPairFull` admin keys + deployment salt and is deferred to admin tooling (post-Stage-2). |
| 8 | `InvitationPanel` (composite — Create / Accept / Responses) | ✅ DONE — `InvitationModel` wires `ContactInvitationService.CreateInvitationAsync` / `IngestInvitationResponsesAsync` against a host-supplied `IAdminInvitationContext` seam. Without the seam (default), the panel renders a "wires post-Stage-2" placeholder. Full Accept / Responses sub-views land alongside the WebAuthn-PRF demo step. |
| 9 | `PushPanel` + `SendMessageDialog` (stub if `IPushNotifier` not yet wired) | ✅ DONE — minimal status panel surfaces whether a real `IPushNotifier` is wired vs the `NullPushNotifier` default. Compose-and-send UI is domain-specific and lives in downstream consumers (messenger, etc.), not in the base library. |
| 10 | `AddCryptoSyncUI()` polish + xUnit project bootstrap | ⏭ Models registered via the RxBlazorV2-generated `ObservableModels.Initialize(services)` (per RxBlazor convention). xUnit `SqliteWasmBlazor.CryptoSync.UI.Tests` project still pending — separate workstream. |
| 2.5a | RxBlazorV2 1.2.3 upgrade + per-command error formatters | ✅ DONE (commits `d8e4fb8` + `7a94ff3`, 2026-04-30). Bumped to 1.2.3, dropped every `try/catch` in command bodies, adopted pessimistic-state pattern for state-recovery cases, dropped per-model `ErrorMessage`/`SuccessMessage` observables. Twelve commands gain `Format*Error` companions (third `[ObservableCommand]` arg) routing through `RxBlazorV2.MudBlazor.Components.StatusModel` (singleton, registered by `AddCryptoSyncUI` via `RxBlazorV2.MudBlazor.ObservableModels.Initialize`). |
| 2.5b | Full en/de UI localization | ✅ DONE (commit pending — see plan `~/.claude/plans/cryptosync-ui-1.2.3-and-localization.md`). Each model + the model-less `PushPanel` injects `IStringLocalizer<T>` via partial constructor or `[Inject]`; sibling `*.resx` (en) + `*.de.resx` ship 80+ keys following the prefix convention (Header_/Btn_/Tooltip_/Status_/Alert_/Error_/Field_/Chip_/Table_/Dialog_/Section_/Default_/Description_). Build emits `bin/.../de/SqliteWasmBlazor.CryptoSync.UI.resources.dll`. Hosts opt in by setting `<BlazorWebAssemblyLoadAllGlobalizationData>true</>` + calling `services.AddLocalization()`. Failure messages from `IDbInitFailure.DefaultMessage` stay English (upstream package, out of scope). |

The goal is a carefully designed re-skinnable Razor library `SqliteWasmBlazor.CryptoSync.UI` that absorbs the existing `BlazorPRF.UI` + `BlazorPRF.Push/Components` panels, rebound onto the in-repo CryptoSync services. **Every panel uses code-behind** (a `*.razor.cs` partial alongside the `.razor` markup) so future re-design / re-skinning into a new MudBlazor / Tailwind / Fluent variant is purely markup work — the behavior layer stays untouched.

**Source panels to absorb:**

| Source | Target | Purpose |
|---|---|---|
| `BlazorPRF.UI/Components/PrfAuthenticate.razor` | `AuthenticationPanel` | WebAuthn assertion + PRF-bound session login. |
| `BlazorPRF.UI/Components/PrfRegistration.razor` | (folded into AuthenticationPanel or a separate `RegistrationPanel`) | First-time WebAuthn credential creation. |
| `BlazorPRF.UI/Components/PublicKeyDisplay.razor` | reusable `PublicKeyDisplay` shared component | Renders pubkey hashes for handoff. |
| `BlazorPRF.UI/Components/SessionExpiredPopover.razor` | reusable `SessionExpiredPopover` | Session-expiry UX. |
| `BlazorPRF.Push/Components/ContactsPanel.razor` | `ContactsPanel` | Contact list / state, bound to `ContactService`. |
| `BlazorPRF.Push/Components/UserProfilePanel.razor` | `UserProfilePanel` | Local identity display + admin-vs-member presentation. |
| `BlazorPRF.Push/Components/InviteLinkCreationPanel.razor` | `InvitationPanel` (creation half) | Admin-side invitation issuance, bound to `ContactInvitationService`. |
| `BlazorPRF.Push/Components/InviteLinkAcceptancePanel.razor` | `InvitationPanel` (acceptance half) | Member-side acceptance + ECDH wrap. |
| `BlazorPRF.Push/Components/CheckResponsesPanel.razor` | folded into `InvitationPanel` (admin tab) | Admin reviews pending invitation responses. |
| `BlazorPRF.Push/Components/PushManager.razor` + `.razor.js` | `PushPanel` | Webpush subscription + send UI, bound to `IPushNotifier`. |
| `BlazorPRF.Push/Components/SendMessageDialog.razor` | folded into `PushPanel` (compose dialog) | Composing a push message body. |
| `BlazorPRF.Push/Components/DatabaseErrorAlert.razor` | reusable `DatabaseErrorAlert` | Boot-status surface for DB / CryptoSync init failures. |

**Design constraints:**

- **Code-behind everywhere.** No logic in `@code { }` blocks. Each `*.razor` is markup-only; each `*.razor.cs` is a `partial class` deriving from `ComponentBase` (or appropriate base) carrying state, lifecycle, and event handlers. This keeps re-skinning into a new design system a markup-only refactor.
- **Service binding via DI, not parameter drilling.** Panels resolve `IWhitelistPushService`, `IAdminPinService`, `ContactService`, etc. through `[Inject]`. Cascading values for theme / locale only.
- **Shared `SqliteWasmBlazor.CryptoSync` already locked.** Panels target the contracts shipped in Stage A — no panel may force a service-shape change without going back to Stage A.
- **Library = pure component library.** No app-layer concerns (routing, layout, auth-redirect glue). Hosting apps wire those in.
- **MudBlazor stays the default skin.** Panels ship MudBlazor markup as the reference implementation per project preference (no hand-rolled CSS where MudBlazor primitives suffice). The code-behind discipline means a re-skin to Fluent / Tailwind doesn't touch behavior.

### Plane separation + test buildup (`plane-separation-and-test-buildup.md`)

- **Plan:** `~/.claude/plans/plane-separation-and-test-buildup.md` (written 2026-05-01).
- **Memory:** `project_plane_separation_status.md`.
- **Trigger:** absorbed BlazorPRF crypto + UI surface mixes base-plane and CryptoSync-plane code in single assemblies (`Crypto.Testing` referenced by production AdminSeed; `CryptoSync.UI` carries panels that only need `Crypto`). Splitting before the Post-Stage-2 demo / TestApp / admin-seeding work prevents new panels and tests from landing in the wrong project.
- **Architectural locks (2026-05-01):** encrypted VFS via PRF is part of the **base plane**, not opt-in. Two planes only: **Base** (SQLite + Blazor + encrypted VFS + PRF) and **CryptoSync** (delta sync + groups + push). Group encryption + WebPush + VAPID stay in the CryptoSync plane (group enc is the multi-recipient sharing primitive used by `GroupService` / `ContactService` / `ContactInvitationService`; WebPush is the wake-the-other-device primitive completing CryptoSync UX). `.Testing` becomes test-only; production AdminSeed gets a separate `SqliteWasmBlazor.Crypto.BouncyCastle` package.

**Phases (executed in order, single branch, no parallel feature work):**

| # | Phase | Status |
|---|---|---|
| 1 | **Structural separation.** Solution folders (`src/Base/`, `src/Crypto/`, `src/CryptoSync/`, `samples/`, `tools/`, `tests/`); carve `SqliteWasmBlazor.Crypto.UI` out of `CryptoSync.UI` (Auth + Registration + DatabaseErrorAlert + SessionExpiredPopover + seams + shared DTOs + en/de resx); split `BouncyCastleCryptoProvider` → new `SqliteWasmBlazor.Crypto.BouncyCastle` (AdminSeed depends on this), `.Testing` dissolved (was empty after the move). _(npm `crypto-core` promotion dropped — path-symlink works, npm is build-tooling-only, JS bundles ship inside NuGets; revisit if external standalone publishing ever becomes a goal.)_ | ✅ DONE — 1.1 Crypto.UI carve-out (`68e825b`); 1.2 BouncyCastle split (`f4499c5`); filesystem reorg (`27da58f`); 4-NuGet consolidation (`da5769b`) — Hosting + Crypto + Crypto.Abstractions folded into SqliteWasmBlazor (base), CryptoSync absorbs Group/Vapid/SigningContext, BouncyCastle + Components/Models/FloatingWindow `IsPackable=false`; 1.4 acceptance verified (build + 322 tests + 4 nupkgs in `.nuget/`); post-consolidation stabilization: `17db161` CS0436 suppression for duplicate MessagePack resolver gen, `85daa44` `AddCryptoSyncCrypto()` split so TestApp can register `IGroupEncryption`/`IVapidCryptoProvider` without HTTP relay transport, `8a4d5f3` repoint `SqliteWasmBlazorCryptoOptions.AssetRoot` to `_content/SqliteWasmBlazor/` after the JS bundle moved into the base NuGet |
| 2 | **Test buildup, three rounds.** R1 (deterministic) ✅ DONE — R1.1 KDF byte-equality (`5fc8a62`); R1.2 canonical envelope agreement + protocol fix `7d77f00` so C# hashes raw ciphertext bytes (matching TS noble); R1.3 `SigningService` keyId-cache; R1.4 `AsymmetricEncryptionService` keyId-cache; R1.5 JS bridge dispatch (storeKeys / signWithCachedKey / encrypt+decrypt round-trips, ttl-null path, AAD binding). All R1.2-R1.5 land in `69c2b41`. **Pre-R2 interop sweep ✅ DONE** — three new vector files mirror xUnit ↔ vitest: (a) `CrossLanguageWrappingKeyVectorTests` / `crossLanguageWrappingKey.test.ts` pin `DeriveWrappingKey` (X25519 ECDH + HKDF) byte-for-byte across three contexts, (b) `CrossLanguageAesGcmVectorTests` / `crossLanguageAesGcm.test.ts` pin AES-256-GCM at fixed nonce (with-and-without AAD) — the underlying primitive of `WrapContentKey`/`UnwrapContentKey`; production wrappers can't be vector-tested directly because they generate a random nonce internally, so the TS side drives `crypto.subtle.encrypt` directly with the fixed nonce, (c) `CrossLanguageEciesVectorTests` / `crossLanguageEcies.test.ts` pin the full ECIES combinator (X25519 ECDH + HKDF "ecies-aes-gcm" + AES-256-GCM at fixed nonce); production `decryptAsymmetric` then round-trips the captured wire bytes back to plaintext. Push payload framing scoped out — `encryptPushPayload` is worker-only with no C# counterpart, no cross-language equality to assert. Sweep was clean; no further interop bugs surfaced. R2 (virtual authenticator E2E): `WaFixtureBase` virtual-authenticator setup + four PRF scenarios (register, unlock+open, mismatch, rekey). R3 (composition): synthetic-PRF-seed → VFS + delta round-trip, sync-engine `KeyRotation` → VFS rekey correctness. Bootstrap `Crypto.UI.Tests` + `CryptoSync.UI.Tests` xUnit projects (absorbs Stage 2 slot 10). | 🟢 R1 + sweep done; R2 + R3 pending |
| 3 | **Resume Post-Stage-2 sequence on the new structure.** (a) Demo with WebAuthn for encryption (folds in Stage B — production wiring of `ISenderAuthSigner` / `IReceiveAuthSigner` against PRF-backed signers). (b) TestApp with WebAuthn end-to-end. (c) WebAuthn-based admin seeding (uses `Crypto.BouncyCastle` for offline seed generation). (d) Further steps TBD (recovery, multi-device admin handoff, etc.). | ⏭ Pending |

The split line for `CryptoSync.UI` panels:

| Panel | Target plane | Reason |
|---|---|---|
| `AuthenticationPanel`, `RegistrationPanel` | **Crypto.UI** (base) | Only need `IPrfAuthenticator` seam |
| `DatabaseErrorAlert`, `SessionExpiredPopover` | **Crypto.UI** (base) | Boot-status / re-auth, no sync coupling |
| `UserProfilePanel` | **CryptoSync.UI** | Injects `DeviceIdentityService` (devices imply sync) |
| `ContactsPanel`, `InvitationPanel` | **CryptoSync.UI** | Inject `ContactService` / `ContactInvitationService` |
| `PushPanel` | **CryptoSync.UI** | `IPushNotifier` lives in `SqliteWasmBlazor.CryptoSync/Services/`; push is wake-the-other-device for sync |

If any Phase 3 step surfaces a wire-protocol issue, it's a Stage A regression — fix in Stage A, re-run the seeded xUnit suite, re-attempt the WebAuthn step.

---

## Postponed

Design-locked work parked with a known revisit trigger.

### Stage 5 — Dual-DB (public + private per device)

- **Plan:** `~/.claude/plans/twin-streams-flowing-codd.md`
- **Memory:** `project_dual_db_architecture.md`
- **Revisit trigger:** Whitelist-broadcast rewrite completes first. The dual-DB inbox dispatcher (commit 4 in the plan) is meaningless without a working sync pump.

Adds a per-device "private" CryptoSync DB alongside the existing "public" one — the user is admin of their own private DB; private contacts don't broadcast. Locked 2026-04-28. Three open questions for the rewrite-aware revision: per-DB whitelist scope vs P2P transport for private DB vs shared whitelist (covered in `relay-whitelist-design.md` §10).

---

## Done

Work that's shipped on `crypto-sync`. Plans kept on disk for git-blame context; no new commits expected.

### Whitelist-broadcast relay rewrite — Stage A (joined sync contract)

- **Plan:** `~/.claude/plans/whitelist-broadcast-rewrite.md`
- **Design:** `docs/security/relay-whitelist-design.md`
- **Audit driver:** `docs/security/relay-audit.md` (closed C-1 through C-4)
- **Final state:** 215 unit + 13 live-relay = 228/228 xUnit green against Herd-served PHP. HEAD `160abac`.

Replaced the per-recipient pubkey-bound delivery model (Stage 3b code) with an admin-pushed pubkey-hash whitelist + broadcast envelope stream + sender-authenticated POST. Closed audit findings C-1 through C-4 by construction; user revocation effective at the network layer; per-recipient metadata removed from `relay.db`. Stage A scope: prove the entire C#↔PHP wire contract end-to-end with **test seeds** (synthetic Ed25519, hardcoded deployment salt, hand-written admin keys). PRF/WebAuthn wiring is Stage B (now Active).

**Steps:**
1. ✅ (`95d0ea2`) — PHP relay rewrite: new schema (whitelist + whitelist_meta + deltas), 3 endpoints (POST whitelist/admin-signed, POST delta/sender-signed, GET delta/pubkey-signed), `cryptosync-relay-init` CLI, `relay-config.example.php`, deny rules in Valet driver + `.htaccess`. W-1 + W-3 + C-2 + C-3 closed in this commit; verification deferred to Step 2's first integration test by design.
2. ✅ (`3952967`) — `ISyncTransport` interface change (drop `recipientPublicKeys[]`) + `ISenderAuthSigner` seam + `HttpSyncTransport` rewrite (POST `{envelope}` + `X-Timestamp`/`X-Sender-PubKey`/`X-Sender-Sig`, GET renamed to `pubkey`/versioned signing strings) + `InMemorySyncRelay/Transport` simplified to FIFO broadcast + all callers (`SyncEngine.PushChangesAsync`, `ContactInvitationService.RespondToInvitationAsync`) migrated + first live-relay integration test (`HttpSyncTransportLiveRelayTests`, `[Trait("Category","LiveRelay")]`) green against Herd-served PHP. 196 xUnit (195 unit + 1 live).
3. ✅ (`c1d32c6`) — `WhitelistPushService` + `DeclarationSigner.SignWhitelistPushAsync`/`VerifyWhitelistPushAsync` + `WhitelistMember`/`WhitelistStatus`/`WhitelistPushResult`/`WhitelistVersionConflictException`. Canonical-string lex-sort byte-identical to PHP's `buildWhitelistSigningString`. 7 fast unit tests + 3 new live-relay scenarios (2-member push, replay → typed conflict, non-whitelisted-sender → 403). Live-relay fixture refactored to delegate to the production service. 206 xUnit.
4. ✅ (4a `64e8d3b` / 4b `7b0ee53` / 4c `e363cf1` / 4d `2a61d73`) — Op-based whitelist contract: PHP `handleWhitelistPush` switched from full-replace to incremental Add/Revoke ops; canonical → `whitelist-ops-v1|version|op1|op2|...`; admin device only tracks `LastWhitelistVersion` (one int) instead of mirroring members. CreateInvitation hook: transport keypair derives dual (X25519 + Ed25519); admin pushes `Add(transport_ed25519_hash)` after persisting invitation. PromoteInvitation hook: `Invitation.TransportEd25519PublicKey` persists the transport hash; promote pushes `[Revoke(transport), Add(contact)]` in one v+1 push. System-admin revocation: `ContactService.RevokeContactAsync` rotates every group the contact's a regular member of (skipping their own self-group), soft-deletes the row, pushes `Revoke(contact_hash)`. `WhitelistAdminFlow.PushAsync` factored as the shared version-tracking + retry-on-409 helper. 213 xUnit (209 unit + 4 live).
5. ✅ (5a `4f1ae1b` / 5b `8197141`) — DI wiring + scenario-completeness sweep (test seeds throughout). 5a: `AddCryptoSync<TContext>` registers `DeclarationSigner` + `IWhitelistPushService` + `IReceiveCursorStore` + `ISyncTransport` against `CryptoSyncOptions.RelayBaseUri` (bindable from appsettings or configure callback); new `EfReceiveCursorStoreFactory<TContext>` wraps `EfReceiveCursorStore` with `IDbContextFactory`; `ISenderAuthSigner` / `IReceiveAuthSigner` stay caller-registered seams (Stage A: stubs; Stage B: PRF-backed). 5b: six new live-relay scenarios — three-actor broadcast, non-admin push → 401, grace-window expired → 403, within-grace GET still drains, body cap (8000 bytes vs 4096) → 413 (C-2 verified end-to-end), stale timestamp → 401. 226 xUnit (215 unit + 11 live).
6. ✅ (`160abac` superseded by `bf177e6` + `0d3f746`) — Final shape: admin-only purge + pinned seed. Original Step 6 introduced `cryptosync-relay-gc.php` (cron-driven, time-based retention) but that was forward-only superseded after two design issues surfaced: (a) autonomous server-side delete decisions clash with the relay's honest-but-curious model; (b) any client-driven compaction reintroduces censorship-by-omission (a malicious whitelisted client could pull all pending patches and post a truncated rollup, erasing patches honest clients hadn't yet received). Replacement: new `deltas.pinned` column, admin-only `X-Admin-Pin-Sig` header on `POST /api/delta` is the sole delete authority, on success in one transaction every prior row is purged + new row stored with `pinned=1`. New `gc_threshold_rows` config (replaces `retention_seconds`); GET responses include informational `gc_requested: true` when unpinned count crosses threshold but the relay never deletes on its own. New C# `IAdminPinService` registered alongside `IWhitelistPushService`; `HttpSyncTransport.LastReceiveSignalledGcRequested` exposes the hint to admin tooling. Two new live-relay scenarios (full operational lifecycle round-trip + non-admin pin → 403) and one new DI assertion. 229 xUnit (215 unit + 13 live + 1 DI). Cost accepted: admin-offline → relay grows past threshold; mitigations (webpush admin-nudge, quorum) captured in Deferred.

**Tiny PHP fixes** from the relay audit (C-2 body cap, C-3 fan-out cap, W-1 `__DIR__` leak, W-3 PDO message leak) landed inside the rewrite, not separately.

### BlazorPRF absorption (`i-did-something-small-parallel-allen.md`)

- **Stage 0 / 0e** — cleanups: npm hardening (`2100398`), Options unification (`474ad0a`), Playwright suite speedup (`cc41657`).
- **Stage 1** — crypto core absorbed: three new in-repo projects (`SqliteWasmBlazor.Crypto.Abstractions`, `SqliteWasmBlazor.Crypto`, `SqliteWasmBlazor.Crypto.Testing`) plus the absorbed TS workspace at `TypeScript/packages/crypto-core/`.
- **Stage 3a** — `ISyncTransport` interface (`55c857e`).
- **Stage 3b** — PHP relay (`DeltaRelay/`) + `HttpSyncTransport` (`6a82118`). _Code currently runs but design now superseded by whitelist-broadcast rewrite._
- **Stage 3c** — `IPushNotifier` interface + null/recording fixtures (`8e121f9`).

### Stage 4 — Invitation flow (`the-way-back-is-peaceful-emerson.md`)

- **Memory:** `project_invitation_flow.md`
- **Commits:** `2c21033` (revert old 4a/4b/4c) → `81ba66f` (Invitation entity + CreateInvitationAsync) → `8a59902` (RespondToInvitationAsync via ECDH wrap) → `e39465d` (IngestInvitationResponsesAsync + PromoteInvitationAsync)
- Admin-initiated, transport-keypair-based. Will need a small update under the rewrite (broadcast POST instead of recipient-list POST) — captured in plan Step 4.

### SyncEngine wiring (Phase A1 + A3) — `project_sync_engine_progress.md`

- **A1** — `SyncEngine` shape + tests (`f1212f4`).
- **A3.1** — push cursor persisted to `SyncState.LastExportedAt` (`6ce1de8`).
- **A3.2** — pluggable `IReceiveCursorStore` on `HttpSyncTransport` (`82ed639`).
- **A3.3** — `EfReceiveCursorStore` durable receive cursor (`ce0c5bf`).
- **A2** — eliminated 2026-04-29 (per-scope envelope splitting moot under broadcast).
- **A4 / A5 / Phase B** — folded into rewrite Step 5.

### Security audit (`docs/security/`)

- **Baseline** — threat model + property catalog (`fba9cbb`).
- **Phase 1** — `GroupService.{Create,Add,Remove}Member` ShareTarget signing (`85f68b6`); closes property catalog P6.
- **Phase 2** — `EfReceiveCursorStore` durable receive cursor (`ce0c5bf`); closes P9.
- **Phase 3** — property tests for P1 / P3 / P11 (`94d0766`); 200-iteration plaintext-leak scan in C# + 4 IND-CPA + 4 nonce-uniqueness in TS.
- **Relay audit + design** — `relay-audit.md` + `relay-whitelist-design.md` (`1416ac5`); ratifies the whitelist-broadcast direction.
- **Catalog state:** 17 covered / 1 partial (P14 deferred) / 0 missing / 0 out-of-scope.

### Crypto memory hygiene — Codex audit close-out (2026-04-30 → 2026-05-01)

- **Driver:** Codex memory-hygiene audit found four classes of leak (VFS plaintext lifetime, C# `headerBytes` not zeroed, worker parsed-header private-key fields not zeroed, JS `binaryHeader` not transferred) plus a deeper architectural finding (private keys cross C#↔JS as immutable Base64 strings — non-zeroable on the JS heap).
- **Memory pattern:** `feedback_crypto_memory_hygiene.md` (post-fix invariants).
- **Architectural follow-up:** `project_base64_boundary_followup.md` (now CLOSED — keyId routing landed).

| commit | scope |
|---|---|
| `5841638` | VFS plaintext finally-clear (`encryptedRead` / `verifyEncryptionKey` / `readSlotPlaintextOrZero`) + C# `headerBytes` `CryptographicOperations.ZeroMemory` + worker parsed-header `clearV2CryptoHeader` helper + `binaryHeader` in postMessage transferable list + C# exception-path `try/finally` for `_keyCache.TryGet` consumers. |
| `f8daa19` | `Ed25519Sign` / `DecryptAsymmetricAesGcm` private key flipped from Base64 string to `[JSMarshalAs<JSType.MemoryView>]` `Span` / `ArraySegment<byte>`. |
| `13a8957` | AES key + content-key plaintext flipped to MemoryView. Refactored to `MemoryMarshal.TryGetArray` so no managed copy of the secret is allocated at the boundary. |
| `3d9de94` | `DeriveWrappingKey` ownPrivateKey flipped to MemoryView. |
| `8305f83` | **keyId architectural migration.** `PrfService.StoreSeedAndDeriveKeysAsync` populates the JS-side `keyCache` via `ICryptoProvider.StoreKeysAsync`; derived keys live in JS as **non-extractable** `SubtleCrypto.CryptoKey` objects (Ed25519 + AES) and a JS-only `Uint8Array` (X25519 priv). C# `SecureKeyCache` retains only the PRF seed for HKDF domain-key derivation. `SigningService` and `AsymmetricEncryptionService` route via `SignWithKeyIdAsync` / `DecryptAsymmetricWithKeyIdAsync` — private keys never re-cross the boundary per operation. |
| `53e18bb` | `.slice()` MemoryView → real `Uint8Array` in TS bridge (noble's `isBytes` requires `instanceof Uint8Array`; the runtime `Span` view fails that). Also fixes the `Task.WhenAny(Expect.ToBeVisibleAsync, …)` failure-swallow bug in `SqliteWasmTestBase` — switched to `Locator.Or`. |
| `a1a14c6` | Regenerate stale `AdminSeed.g.cs` — committed PermissionTableSignature hash diverged from current generator output. |
| `5541a14` | Map `PERMISSION_SENDER_UNAUTHORIZED` → wire int 14 in `importErrorCodeToInt` (commit `42bc26a` added the C# enum value but missed the TS mapping; tests silently failed via `default: return 99`). Revert over-bumped Playwright timeout to 60s now that the wrapper actually surfaces failures. |
| `caf334f` | **Second-pass Codex audit close-out (2026-05-01).** PRF seed `StoreKeysAsync` flipped to `MemoryView` (no Base64 string); `encryptAesGcmB64` clears `ptCopy` in finally (CEK-after-wrap window); HKDF-derived `wrappingKeyResult.Value` now `try/finally` cleared at every consumer (7 sites in `GroupEncryptionService`, 2 in `ContactInvitationService`); `DeriveDualKeyPair` seed flipped to `MemoryView` and the unpacked private-key buffer `CryptographicOperations.ZeroMemory`-ed in finally. `DualKeyPairFull` private-key string surface logged as known architectural debt (blocked by AdminSeed.g.cs codegen template). |
| `3884ad7` | **Third-pass Codex audit close-out (2026-05-01).** Three findings closed. (a) Secret return buffers in TS B64 bridges (`deriveDualKeyPairB64`, `decryptAesGcmB64`, `deriveWrappingKeyB64`) wrap the `Uint8Array` result in scoped `try/finally` and `clearBytes` after `bytesToBase64` so derived wrapping keys, unwrapped CEKs, and packed dual private keys don't survive until GC. (b) `storeKeys()` failure paths now wrap each derived secret (`ed25519Seed`, `pkcs8Key`, `symmetricKey`) in scoped finally blocks; the X25519 private key is only retained when `keyCache.set` succeeds, otherwise the outer finally zeroizes it — a throw from any `crypto.subtle.importKey/exportKey` no longer leaves derived secret temporaries on the JS heap. (c) Legacy single-key derivation APIs (`DeriveX25519KeyPairAsync`/`DeriveEd25519KeyPairAsync` + B64 TS wrappers) had zero production callers and are removed entirely from `ICryptoProvider`, `NobleCryptoProvider`, `BouncyCastleCryptoProvider`, `NobleInterop`, `crypto.ts`, and `index.ts`; dead `KeyGenerator` (string) overloads removed too. |

**End state:** every per-operation crypto path uses `MemoryView` for secrets; sign + ECIES decrypt route through the JS keyId cache (Ed25519 = non-extractable CryptoKey); HKDF wrapping keys + bridge plaintext slices + packed unpack buffers all zeroed deterministically; recurring zeroization invariants codified in feedback memory; Playwright suite no longer silently passes failed crypto tests; AdminSeed-regeneration trigger captured in feedback memory.

---

## Deferred

Captured follow-ups with no plan file written. Tracked here so they don't fall off; revisit when relevant.

- **Audit P14** — permission-denied-row-leaves-no-shadow PBT. Deferred because `applyShadowRowGroup` is tightly coupled to `worker-state.openDatabases` and the full sqlite-wasm + shadow-table schema. Either a node-side wasm harness or a refactor to inject the DB dependency. Browser-side smoke tests cover this path end-to-end today.
- **Webpush admin nudge for `gc_requested`.** Preferred mitigation for the admin-offline gap, deferred until the upcoming webpush integration (Stage 3c `IPushNotifier` exists; production wiring is Stage B+). Clients that observe `gc_requested: true` persistently (e.g. across N polls or X minutes — domain-tunable) emit an admin-targeted push notification asking the admin to come reseed. Admin-only purge authority stays unchanged at the wire layer; clients only *nudge*. No new relay endpoints, no new signing strings, no new wire config — purely a domain-level reaction in the C# / TS clients on top of the `LastReceiveSignalledGcRequested` flag already exposed by `HttpSyncTransport`. This is the lightest-weight path and folds into infrastructure already on the roadmap.
- **Quorum / multi-party compaction.** Heavier alternative to the webpush nudge for the same admin-offline gap. M-of-N whitelisted peers co-sign a compacted rollup; relay verifies M distinct attestations before accepting the purge. Closes the gap *and* preserves the client-only architecture without requiring admin presence at all, but needs three new things this codebase doesn't have: peer-discovery, off-relay attestation exchange, and operational quorum tuning (~1-2 weeks of work). Weighted variant ("admin sig counts as M-1, peer sigs count as 1") gives admin-online behavior identical to today plus admin-offline graceful degradation; same plumbing cost. Likely overkill if the webpush nudge above proves sufficient in practice.
- **Admin-role rethink for unattended automation.** Stage B will bind admin authority to a hardware-backed WebAuthn passkey via PRF. That cryptographic choice means admin actions cannot run as cron / systemd / launchd jobs (no human, no passkey gesture). Any future "automatic compaction at off-peak" plan needs a separate admin-compaction key (or delegated identity) distinct from the passkey-bound primary admin sig. Defer until a concrete operational need surfaces; today's admin-online-when-needed model is acceptable.
- **Snapshot endpoints / retention GC** (relay-side). Original `project_relay_design.md` follow-up. Now partially obsoleted by the pinned-seed mechanism (admin reseed = a snapshot). The remaining gap is automated retention without admin involvement, which folds into the two items above.
- **Signed admin key bundle** in PRF/WebAuthn flow. For admin device backup. Domain concern, not protocol concern. Captured in `relay-whitelist-design.md` §10.
- **Per-pair sender→recipient routing** at relay. Optional metadata tightening. Not in the rewrite scope.
- **Sender-rate-limiting at relay** by pubkey hash. Useful if a whitelisted device is compromised and abused.
- **Per-CEK message-count cap** for nonce-collision-bound rotation. Scale-driven; current threat is below the 2^32-message-per-key threshold.
- **VFS export rekey + import verify-on-write** (`project_vfs_export_rekey.md`). Design-locked 2026-04-26; not yet implemented. Slot-rekey primitive for plain export, rekeyed export to a caller-supplied K_new, and import verify-on-write.
- **Demo build / migration-recovery tests / PRF AEAD verify tests** (`project_boot_status_followups.md`). Pending after typed boot status (PR1) + CryptoSync stage (PR2) shipped.
- **Pre-release README rewrite** (`project_pre_release_readme_todo.md`). Contributor-only caveats accumulating from cleanup commits (e.g. `npx patch-package` after cold install).

---

## Plans on disk vs status

| Plan file | Status | Pointer |
|---|---|---|
| `plane-separation-and-test-buildup.md` | **ACTIVE** | this document → Active |
| `cryptosync-ui-absorption.md` | DONE (slots 1-9 + 2.5a + 2.5b ✅; slot 10 absorbed into plane-separation Phase 2) | this document → Active (transitional) |
| `whitelist-broadcast-rewrite.md` | DONE (Stage A); Stage B folded into plane-separation Phase 3 | this document → Done |
| `twin-streams-flowing-codd.md` | **POSTPONED** | this document → Postponed |
| `i-did-something-small-parallel-allen.md` | DONE (master plan, kept for context) | this document → Done |
| `the-way-back-is-peaceful-emerson.md` | DONE | this document → Done |
| `nifty-tickling-lemur.md` | DONE + design superseded (Stage 3 origin doc) | historical only |

Other plan files in `~/.claude/plans/` belong to older workstreams or other projects and are not part of CryptoSync's active state.

---

## Numbering system retirement

After the whitelist-broadcast rewrite completes, drop `Stage X` / `Phase A` / `Phase B` / `Phase C` / `Audit Phase N` numbering entirely. Going forward, work is referenced by **named plan file** (e.g. `whitelist-broadcast-rewrite.md`) and listed under one of the four buckets in this document. The numbered systems were useful while their respective workstreams were in flight; once a workstream lands, its numbering is preserved in commit messages and historical plan files but doesn't propagate forward.
