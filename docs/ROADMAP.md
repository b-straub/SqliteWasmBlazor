# SqliteWasmBlazor Roadmap

_Single source of truth for "where are we." Last updated 2026-04-29 against branch `crypto-sync` HEAD pending Step 6 redesign. Stage A of the whitelist-broadcast rewrite is in flight — Step 6 ("retention" → "admin-only purge with pinned seed") is being landed as a redesign of the prior cron-based GC commit. Stage B (production identity wiring) follows once Stage A is green._

This document supersedes the multiple parallel numbering systems (Stage / Phase A / Phase B / Audit Phase 1-3) that were used while individual workstreams were in flight. Going forward, work is grouped only as **Active**, **Postponed**, **Done**, **Deferred**.

If you're picking this up cold, read in this order:
1. This document (5 min) — bird's-eye view.
2. `docs/security/threat-model.md` (5 min) — policy stance that locks several catalog verdicts.
3. `docs/security/property-catalog.md` (10 min) — 18-property security audit; 17/18 covered, 1 deferred.
4. The plan file under **Active** below (10 min) — the actual next-step script.

---

## Active

### Stage A Step 6 redesign — admin-only purge + pinned seed

- **Plan:** `~/.claude/plans/whitelist-broadcast-rewrite.md` § Step 6 (in-place revision; the original cron-driven retention model is being replaced).
- **Status:** code in flight; commit `160abac` ("GC CLI + retention test") and `ffcf5a2` ("docs: Stage A Step 6 done") are being **superseded** in a forward-only commit (no rebase) that drops the cron CLI and the time-based retention model.
- **Estimated effort:** ~half a day.

The original Step 6 introduced `cryptosync-relay-gc.php` (cron-driven, time-based retention) and a `retention_seconds` config knob. Two design issues surfaced in review:

1. **Autonomous server-side decisions break the honest-but-curious model.** The relay deciding "this row is too old, delete it" is a server-side policy decision, even if time-based. The user prefers all delete authority to flow from a client-signed action.
2. **Client-driven compaction reintroduces censorship-by-omission.** A first-pass design considered "any whitelisted client can post a compacted rollup that triggers a server-side purge of patches older than the rollup's `up_to_cursor`." That gives any malicious whitelisted client the ability to pull all pending patches, post a *truncated* rollup, and erase patches that other honest clients hadn't yet received. Censorship masquerading as compaction. Untenable.

The replacement model is **admin-only purge** with a pinned seed:

- New `deltas.pinned` column. The admin's pin POST (header `X-Admin-Pin-Sig` over `deltapin-v1|ts|envelope_hash`) is the *sole* delete authority on the relay: in one transaction, every prior row is purged and the new envelope is stored with `pinned=1`. The pin POST verifies sender hash equals deployment `admin_pubkey_hash` before any sig work, so a non-admin attempt → 403 before crypto.
- New `gc_threshold_rows` config (replaces `retention_seconds`). The relay never deletes on its own; instead, when `COUNT(*) FROM deltas WHERE pinned = 0` exceeds the threshold, every `GET /api/delta` response includes `"gc_requested": true`. Purely informational — the admin client reads it and may respond with a fresh pin POST. Non-admin clients observe the flag for diagnostics but cannot act on it.
- `cryptosync-relay-gc.php` deleted. No cron / systemd / launchd plumbing required.
- New C# `IAdminPinService` registered in DI alongside `IWhitelistPushService`. `HttpSyncTransport` exposes `LastReceiveSignalledGcRequested` so admin tooling can react to the hint.

**Tests:** the prior GC retention test + empty-DB GC test are deleted. Replaced with `SeedReseed_GcSignalDrivesAdminCompaction_OperationalRoundTrip` (admin seeds → patches accumulate past threshold → relay flags `gc_requested` → admin reseeds → fresh receiver bootstraps off new seed) and `Pin_NonAdminSender_Returns403` (censorship attack denied at the wire layer). Live-relay suite stays at 13 tests; total xUnit count holds at 229 = 215 unit + 13 live + 1 new DI assertion for `IAdminPinService` registration.

### Stage B — Production identity wiring

- **Plan:** folded into `~/.claude/plans/whitelist-broadcast-rewrite.md` (§ Out of scope — Stage B). Standalone plan file to be written when Stage B work begins.
- **Trigger:** Stage A Step 6 redesign green.
- **Estimated effort:** 1-2 days.

Stage A proves the C#↔PHP wire contract end-to-end with test seeds. Stage B swaps the stub `ISenderAuthSigner` / `IReceiveAuthSigner` for PRF-backed implementations sourced from WebAuthn. Browser host (`SqliteWasmBlazor.Demo`) registers the production signers in DI; a Playwright smoke test confirms the same scenarios as the Stage A xUnit suite, but with real WebAuthn identities. No protocol-level work; purely DI + JS interop.

If Stage B uncovers a wire-protocol issue, it's a Stage A regression — fix in Stage A, re-run seeded suite, re-attempt Stage B.

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
6. ✅ (`160abac`) — **SUPERSEDED.** Original Step 6 introduced `cryptosync-relay-gc.php` (cron-driven, `retention_seconds` config). Replaced in flight by the admin-only-purge redesign — see Active. The supersession is forward-only (no rebase); commit history retains both for context.

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

---

## Deferred

Captured follow-ups with no plan file written. Tracked here so they don't fall off; revisit when relevant.

- **Stage 2 — UI absorption** (was Phase C). `BlazorPRF.UI` panels (`AuthenticationPanel`, `ContactsPanel`, `UserProfilePanel`, `InvitationPanel`, `PushPanel`) absorbed as `SqliteWasmBlazor.CryptoSync.UI`. Deferred per user preference for xUnit-testable slices over UI work.
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
| `whitelist-broadcast-rewrite.md` | **ACTIVE** (Stage B § "Out of scope — Stage B"); Stage A DONE | this document → Active + Done |
| `twin-streams-flowing-codd.md` | **POSTPONED** | this document → Postponed |
| `i-did-something-small-parallel-allen.md` | DONE (master plan, kept for context) | this document → Done |
| `the-way-back-is-peaceful-emerson.md` | DONE | this document → Done |
| `nifty-tickling-lemur.md` | DONE + design superseded (Stage 3 origin doc) | historical only |

Other plan files in `~/.claude/plans/` belong to older workstreams or other projects and are not part of CryptoSync's active state.

---

## Numbering system retirement

After the whitelist-broadcast rewrite completes, drop `Stage X` / `Phase A` / `Phase B` / `Phase C` / `Audit Phase N` numbering entirely. Going forward, work is referenced by **named plan file** (e.g. `whitelist-broadcast-rewrite.md`) and listed under one of the four buckets in this document. The numbered systems were useful while their respective workstreams were in flight; once a workstream lands, its numbering is preserved in commit messages and historical plan files but doesn't propagate forward.
