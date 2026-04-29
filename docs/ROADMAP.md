# SqliteWasmBlazor Roadmap

_Single source of truth for "where are we." Last updated 2026-04-29 against branch `crypto-sync` HEAD `3952967`._

This document supersedes the multiple parallel numbering systems (Stage / Phase A / Phase B / Audit Phase 1-3) that were used while individual workstreams were in flight. Going forward, work is grouped only as **Active**, **Postponed**, **Done**, **Deferred**.

If you're picking this up cold, read in this order:
1. This document (5 min) — bird's-eye view.
2. `docs/security/threat-model.md` (5 min) — policy stance that locks several catalog verdicts.
3. `docs/security/property-catalog.md` (10 min) — 18-property security audit; 17/18 covered, 1 deferred.
4. The plan file under **Active** below (10 min) — the actual next-step script.

---

## Active

The only piece currently in flight.

### Whitelist-broadcast relay rewrite — Stage A (joined sync contract)

- **Plan:** `~/.claude/plans/whitelist-broadcast-rewrite.md`
- **Design:** `docs/security/relay-whitelist-design.md`
- **Audit driver:** `docs/security/relay-audit.md` (4 Critical / 3 Warning / 3 Info findings)
- **Estimated effort:** ~4.5 days, 6 sequenced steps each independently shippable

Replaces the per-recipient pubkey-bound delivery model (Stage 3b code, currently running) with an admin-pushed pubkey-hash whitelist + broadcast envelope stream + sender-authenticated POST. Closes audit findings C-1 through C-4 by construction; makes user revocation effective at the network layer; eliminates per-recipient metadata in `relay.db`.

**Stage A scope:** prove the entire C#↔PHP wire contract end-to-end against dev PHP using **test seeds** (synthetic Ed25519 keypairs, hand-written admin keys, hardcoded deployment salt). No PRF, no WebAuthn, no browser host. Stage B (production identity wiring) is a separate workstream — see Postponed.

**Steps:**
1. ✅ **DONE** (`95d0ea2`) — PHP relay rewrite: new schema (whitelist + whitelist_meta + deltas), 3 endpoints (POST whitelist/admin-signed, POST delta/sender-signed, GET delta/pubkey-signed), `cryptosync-relay-init` CLI, `relay-config.example.php`, deny rules in Valet driver + `.htaccess`, W-1 + W-3 + C-2 + C-3 closed in this commit. Verification deferred to Step 2's first integration test by design.
2. ✅ **DONE** (`3952967`) — `ISyncTransport` interface change (drop `recipientPublicKeys[]`) + `ISenderAuthSigner` seam + `HttpSyncTransport` rewrite (POST `{envelope}` + `X-Timestamp`/`X-Sender-PubKey`/`X-Sender-Sig`, GET renamed to `pubkey`/versioned signing strings) + `InMemorySyncRelay/Transport` simplified to FIFO broadcast + all callers (`SyncEngine.PushChangesAsync`, `ContactInvitationService.RespondToInvitationAsync`) migrated + first live-relay integration test (`HttpSyncTransportLiveRelayTests`, `[Trait("Category","LiveRelay")]`) green against Herd-served PHP. 196 xUnit tests pass (195 unit + 1 live).
3. ⏭ **NEXT** — `WhitelistPushService` (admin-signed pubkey-hash list push) + integration scenario.
4. Hooks into `ContactInvitationService.{Create,Promote}InvitationAsync`, system-admin revocation flow (TBD where it lives — open question in plan).
5. DI wiring + scenario-completeness sweep with test seeds (three-actor sync, replay defense, grace-window, body cap, timestamp window).
6. GC CLI (`cryptosync-relay-gc.php`) + time-based retention test.

**Stage A is green when all of Steps 1-6 land and the integration suite passes against Herd-served PHP.**

**Tiny PHP fixes** from the relay audit (C-2 body cap, C-3 fan-out cap, W-1 `__DIR__` leak, W-3 PDO message leak) **do not land separately** — they appear naturally inside the rewrite.

---

## Postponed

Design-locked work parked with a known revisit trigger.

### Whitelist-broadcast Stage B — Production identity wiring

- **Plan:** folded into `~/.claude/plans/whitelist-broadcast-rewrite.md` (§ Out of scope — Stage B). Standalone plan file written when Stage A is green.
- **Revisit trigger:** Stage A complete (all 6 steps merged, integration suite green against Herd-served PHP).
- **Estimated effort:** 1-2 days

Once Stage A proves the C#↔PHP wire contract end-to-end with test seeds, Stage B swaps the stub `ISenderAuthSigner` / `IReceiveAuthSigner` for PRF-backed implementations sourced from WebAuthn. Browser host (`SqliteWasmBlazor.Demo`) registers the production signers in DI; a Playwright smoke test confirms the same scenarios as the Stage A xUnit suite, but with real WebAuthn identities. No protocol-level work; purely DI + JS interop.

If Stage B uncovers a wire-protocol issue, it's a Stage A regression — fix in Stage A, re-run seeded suite, re-attempt Stage B.

### Stage 5 — Dual-DB (public + private per device)

- **Plan:** `~/.claude/plans/twin-streams-flowing-codd.md`
- **Memory:** `project_dual_db_architecture.md`
- **Revisit trigger:** Whitelist-broadcast rewrite completes first. The dual-DB inbox dispatcher (commit 4 in the plan) is meaningless without a working sync pump.

Adds a per-device "private" CryptoSync DB alongside the existing "public" one — the user is admin of their own private DB; private contacts don't broadcast. Locked 2026-04-28. Three open questions for the rewrite-aware revision: per-DB whitelist scope vs P2P transport for private DB vs shared whitelist (covered in `relay-whitelist-design.md` §10).

---

## Done

Work that's shipped on `crypto-sync`. Plans kept on disk for git-blame context; no new commits expected.

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
- **Snapshot endpoints / retention GC** (relay-side). Original `project_relay_design.md` follow-up. Whitelist solves auth; snapshot solves storage-bound. Independent from the rewrite — can land before, during, or after.
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
| `whitelist-broadcast-rewrite.md` | **ACTIVE** | this document → Active |
| `twin-streams-flowing-codd.md` | **POSTPONED** | this document → Postponed |
| `i-did-something-small-parallel-allen.md` | DONE (master plan, kept for context) | this document → Done |
| `the-way-back-is-peaceful-emerson.md` | DONE | this document → Done |
| `nifty-tickling-lemur.md` | DONE + design superseded (Stage 3 origin doc) | historical only |

Other plan files in `~/.claude/plans/` belong to older workstreams or other projects and are not part of CryptoSync's active state.

---

## Numbering system retirement

After the whitelist-broadcast rewrite completes, drop `Stage X` / `Phase A` / `Phase B` / `Phase C` / `Audit Phase N` numbering entirely. Going forward, work is referenced by **named plan file** (e.g. `whitelist-broadcast-rewrite.md`) and listed under one of the four buckets in this document. The numbered systems were useful while their respective workstreams were in flight; once a workstream lands, its numbering is preserved in commit messages and historical plan files but doesn't propagate forward.
