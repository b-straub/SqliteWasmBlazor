# CryptoSync Threat Model

_Last regenerated: 2026-04-28 by `/cryptosync-audit threat-model`. Source whitepaper: `cryptosync-verification-whitepaper.md` §2. Trust the code over the memories: any divergence is reported below._

## 1. System Sketch

<!-- audit-keep -->
<!-- /audit-keep -->

CryptoSync is a fully serverless, end-to-end encrypted Blazor WebAssembly application: every device hosts the only authoritative copy of its own data, and no plaintext database file ever travels between devices or from a server. Per memory `project_serverless_architecture.md`, the only persistent backing is OPFS in the browser; `SqliteWasmBlazor.TestHost` is a dev-only static-asset host with no API, and `AdminSeed` is a desktop .NET CLI that emits C# source files (`AdminSeed.g.cs`), not databases.

The runtime topology is:

- **Single SQLite engine in the Web Worker.** Per memory `project_real_architecture.md`, the .NET side carries an 8 KB stub (`SqliteWasmBlazor/native/sqlite3_stub.c` → `e_sqlite3.a`) that satisfies SQLitePCLRaw P/Invoke; every SQL call from .NET is forwarded via `SqliteWasmWorkerBridge` to the worker. The worker (`SqliteWasmBlazor/TypeScript/worker/sqlite-worker.ts`) runs the real `sqlite3.wasm` against an OPFS SAHPool VFS — encrypted page-by-page when a key is registered (`SqliteWasmBlazor/TypeScript/worker/vfs-prf/sahpool-prf-vfs.ts`).
- **Sync transport** is `HttpSyncTransport` (`SqliteWasmBlazor.CryptoSync/Services/HttpSyncTransport.cs`) against the PHP relay `DeltaRelay/delta-relay.php`. The relay never inspects payloads and stores opaque ciphertext blobs indexed by recipient pubkey.
- **Crypto runs in the worker.** Per memory `feedback_architecture.md`, `crypto-ops.ts` owns decrypt/apply/permission-enforcement; C# is a thin bridge that constructs `V2CryptoHeader` envelopes and never sees CEKs.
- **Invitation flow** is the Stage 4 "ShareGroup with transport keypair" design (memory `project_invitation_flow.md`, locked 2026-04-28). The bundle carries a 32-byte transport secret OOB; both sides derive the same X25519 keypair, the relay routes by the transport pubkey, and the existing V2 envelope path delivers the response.

## 2. Actors

| Actor | Trust level | Capabilities |
|---|---|---|
| Local device user | Trusted | Holds primary authenticator (passkey + WebAuthn PRF). |
| Other paired devices | Trusted up to revocation | Hold their own subkeys derived from their own PRF seed. Compromise of one is bounded by P16. |
| Relay (`DeltaRelay/delta-relay.php`) | Honest-but-curious | Stores ciphertext BLOBs in `relay.db`, signs nothing, holds no keys. May log requests, may serve old ciphertexts, may collude with a network attacker. |
| Network attacker | Active Dolev-Yao | May intercept, modify, drop, replay, reorder. Bounded by HTTPS at the transport layer; treated as full Dolev-Yao at the application layer. |
| Endpoint malware | Out of scope (whitepaper §2). |
| Hardware-level side channels | Out of scope. |

## 3. In-Scope Threats

1. Honest-but-curious relay reads ciphertext + metadata + access patterns.
2. Active network attacker modifies, drops, replays, or reorders sync messages.
3. Compromise of a single client device (must not break confidentiality of other devices' future content — see §8 "Revocation effectiveness").
4. Multiple concurrent clients with stale views.
5. Relay-side metadata leaks (logs, error messages, filenames, timing).
6. Relay-side surface overload / DoS.

## 4. Explicitly Out of Scope

1. Compromise of the user's primary authenticator (passkey / WebAuthn PRF master).
2. Side channels at the device-hardware level.
3. Endpoint malware reading plaintext post-decryption.
4. Long-term post-quantum threats. **Current primitives** (verified against `SqliteWasmBlazor.Crypto/TypeScript/packages/crypto-core/src/`):
   - **AEAD per-row (delta envelopes):** AES-256-GCM via WebCrypto SubtleCrypto (`aesGcm.ts`).
   - **AEAD per-page (at-rest VFS):** ChaCha20-Poly1305 via `@noble/ciphers` (`sahpool-prf-vfs.ts`).
   - **ECDH:** X25519 via `@noble/curves` (`x25519.ts`).
   - **KDF:** HKDF-SHA-256 (`PrfService.cs`, `ecies.ts`).
   - **Signatures:** Ed25519 via `@noble/curves` (and BouncyCastle on the .NET test side).
5. Payload confidentiality at the relay (relay only sees opaque ciphertext; not a meaningful audit target).
6. Cryptographic primitive correctness. Per whitepaper §3.3 the strategy is "use vetted libraries"; this project uses `@noble/*` plus BlazorPRF wrappers.

## 5. Trust Boundaries

| Boundary | Crossed by | Authentication mechanism |
|---|---|---|
| Worker ↔ Main thread | `postMessage` over a same-origin channel | Same-origin browser context (no cross-origin attacker model). |
| Device ↔ Relay (POST) | HTTPS POST `/api/delta` | **None at transport layer** — `delta-relay.php:77-117` accepts any request matching the schema. Confidentiality and authenticity come from the V2 envelope's per-row AES-GCM AAD, per-group Ed25519 signature, and outer Ed25519 signature. |
| Device ↔ Relay (GET) | HTTPS GET `/api/delta?recipient=PK&since=N` | `IReceiveAuthSigner` signs `"{ts}\|{recipient}"` with the recipient's Ed25519 priv; relay verifies via `sodium_crypto_sign_verify_detached` against the recipient pubkey from the query (`delta-relay.php:127-145`); ±300 s clock-skew window. |
| Device ↔ Device (via relay) | MessagePacked `DeltaEnvelope` | Outer Ed25519 signature over `pack(groups)` + per-group batch Ed25519 signature + per-row AES-GCM AAD `"{groupContext}:{keyVersion}"`. Receive cursor monotonicity from `IReceiveCursorStore`. |
| Invitation: inviter ↔ invitee | `InvitationBundle` carried OOB | OOB transport secret (32 B) → both sides derive same X25519 keypair → standard V2 envelope path delivers the response. Bundle carries an Ed25519 signature from the admin over `transportPub \| GroupId \| ExpiresAt` (verified at `InvitationRoundtripTests.cs:20-35`). |

## 6. Key Hierarchy

Derived from `SqliteWasmBlazor.Crypto/Services/PrfService.cs`, `SqliteWasmBlazor/TypeScript/worker/vfs-prf/key-registry.ts`, `vfs-prf/rekey.ts`, and `crypto-ops.ts`.

```
WebAuthn passkey + PRF eval (browser)
  └── 32 B PRF seed (cached in SecureKeyCache; never persisted)
        ├── HKDF-SHA-256(info=domainContext) → 32 B per-domain key  (PrfService.DeriveDomainKeyAsync)
        ├── deriveDualKeyPair(seed)
        │     ├── X25519 priv (32 B)  → identity ECDH
        │     └── Ed25519 priv (32 B) → identity signature key
        │
        └── (cached: PRF seed; cleared by PrfService.ClearKeys / DisposeAsync)

Per-DB at-rest key (registered in vfs-prf/key-registry.ts before xOpen):
  └── 32 B ChaCha20-Poly1305 key (origin: caller-supplied, typically a derived
        domain key or a freshly-generated bytes_32 the host stashes;
        clearBytes() wipes it on close)

Per-group CEK (32 B AES-256-GCM):
  ├── Generated per-group on creation (RotateGroupKeyAsync / CreateGroupKeysAsync)
  ├── Wrapped per-member via ECIES = X25519(memberPub, adminPriv) + HKDF + AES-GCM
  ├── Stored as `ShareTarget.WrappedContentKey` ([nonce(12)|ciphertext])
  └── AAD on per-row encrypt: `${groupContext}:${keyVersion}` (crypto-ops.ts:150-152)

Per-page VFS nonce: 12 B random per encrypt; aad = `prf-vfs-v1|dbPath|slotIndex_LE32`.
Per-row delta nonce: 12 B random per encrypt (aesGcm.ts:26).
Per-receive-challenge nonce: implicit via timestamp (relay window = 300 s).
```

Domain separation between the four contexts (at-rest VFS / per-row delta / wrapping-key / signing) is established by:
1. Disjoint key-derivation paths (PRF seed vs ECIES output vs caller-supplied at-rest key).
2. Distinct AAD prefixes (`prf-vfs-v1|...` vs `groupContext:keyVersion`).
3. Distinct algorithms (ChaCha20-Poly1305 at-rest vs AES-256-GCM on-wire).

## 7. Sync State Machine

Derived from `SqliteWasmBlazor.CryptoSync/Services/SyncEngine.cs`, `SyncOrchestrator.cs`, `IReceiveCursorStore.cs`, `HttpSyncTransport.cs`.

```
                  SyncEngine.SyncOnceAsync(ownKeys)
                              │
                              ▼
                       PushChangesAsync ──────► PullChangesAsync
                              │                     │
        ┌─────────────────────┴────────────┐        │
        │                                  │        │
        ▼                                  ▼        ▼
   GetLastExportedAt    EnumerateRecipients   while TryReceiveAsync != null
   (SyncStates row,                                  │
    EngineCursorId)                                  ▼
        │                                       ImportAsync
        ▼                                       (orchestrator → worker)
   ExportAsync                                       │
   (orchestrator → worker)                           ▼
        │                                       Total rows applied returned
        ▼
   transport.SendAsync(envelope, recipients)
        │
        ▼
   SaveCursorAsync(now)  ── persists LastExportedAt
```

**Cursor advance contract:**

- **Export cursor (`SyncState.LastExportedAt`)**: monotonically increases each successful push (`SyncEngine.cs:107, 122`); persisted to `SyncStates` (`Models/SyncState.cs`); survives process restart (covered: `SyncEngineTests.PushChanges_CursorPersistsAcrossEngineInstances`).
- **Receive cursor (`IReceiveCursorStore`)**: advances only forward (`HttpSyncTransport.cs:122-141`). Default impl `InMemoryReceiveCursorStore` does **not** persist — production hosts must inject an OPFS-backed impl. Until they do, every reload replays the inbox from `since=0`. (Status per memory `project_relay_design.md` open follow-up #3.)
- **Relay-side cursor**: `cursor INTEGER PRIMARY KEY AUTOINCREMENT` in `DeltaRelay/delta-relay.php:56-58`. Strictly increasing, single source of truth for ordering across the inbox.

States: `{idle, pushing, pulling, applying-system-stage, applying-domain-stage}`. There is no "merging" state — receivers always run DeltaWins (see §8).

## 8. Stated Policies (used by the property catalog)

These are intentional design choices that affect property verdicts. **Edit as policy evolves.**

- **Conflict resolution**: **DeltaWins is the explicit transport policy.** `SqliteWasmBlazor/TypeScript/worker/bulk-ops.ts:59` ("DeltaWins: always overwrite") and `crypto-ops.ts:1160-1163` (delta import always passes `mode=3`). Per memory `project_conflict_resolution.md`, column-level merge with permission gating is desired but **not yet implemented**; the policy of record today is DeltaWins. P13 verdict: Covered (by stated policy). P15 verdict: Covered (by stated policy).
- **Last-write-wins**: **Explicit, last-arriving-at-receiver.** Ordering is the relay-cursor order at each receiver (not an intra-envelope `UpdatedAt` tie-break). A late envelope from a slow client overwrites a faster client's later write. Acceptable trade-off per the DeltaWins policy.
- **Full local snapshot**: **Receiver read permission is not a sync-layer materialization gate today.** Every paired client that holds the relevant group CEK may carry and apply the full encrypted database snapshot for that group, regardless of whether its role's `SyncPermission.CanRead` is true for a given table. The current worker import path uses permissions for **sender mutation authorization** (insert/update/delete and column update constraints), not for receiver-side row hiding. P14 is therefore evaluated under this policy: CryptoSync does not currently promise a receiver cannot materialize a row if it has the CEK and the full snapshot.
- **Re-encryption on grant**: **Future content only.** Adding a member to a group bumps `KeyVersion`, generates a fresh CEK, and re-wraps for all current members (`GroupService.cs:158-193`, `GroupTransferService.cs:231-302`). **Historical envelopes encrypted under the old `KeyVersion` are not re-shipped** to the new member. New members joining via invitation flow receive future deltas only; on first sync they pull whatever the relay still has buffered for them, encrypted under the current `KeyVersion`. Determines P17 phrasing: "without re-encrypting all historical data" is satisfied because historical re-encryption never happens — the trade-off is that a brand-new device cannot recover any state predating its `ShareTarget`.
- **Revocation effectiveness**: **Effective only after the next manual key rotation.** Today there is no automatic linkage from `LeaveDeclaration` or `RevocationDeclaration` to `GroupService.RemoveMemberAsync`. A revoked or departed member retains the ability to decrypt every envelope addressed to their pubkey at the pre-rotation `KeyVersion` until an admin runs `RemoveMemberAsync` (or `GroupTransferService.ClaimGroupAsync` in the override-transfer case). Policy of record: **the admin SHOULD rotate immediately on observing a leave/revocation**; the application MAY surface this in UI but is not required to enforce it. P16 verdict under this policy: Covered (assuming admin rotates). P18 verdict under this policy: Covered (by stated policy). Future tightening: auto-rotation on declaration import is tracked as Phase 5 in the remediation plan.
- **Replay window at the relay**: **Within ±300 s, replay is acceptable.** `delta-relay.php:127-145` validates the `X-Timestamp` is within ±300 s of relay clock and verifies the Ed25519 signature; **there is no per-pubkey nonce store**. An attacker who sniffs a `(X-Timestamp, X-Sig)` pair can replay it within the window to refetch envelopes addressed to the same pubkey. Since those envelopes are already AEAD-protected and addressed to that pubkey, the leaked information is bounded to "same content the legitimate client could fetch." Policy of record: this is acceptable. P8 verdict under this policy: Covered (by stated policy). Future tightening: per-pubkey nonce table at relay is tracked as Phase 4 in the remediation plan.
- **Metadata classification**:
  - **Routing (visible to relay):** recipient pubkey (every request), sender Ed25519 pubkey (envelope header), `groupContext`, `keyVersion`, `SharingScope`, `SharingId`, row `Id`, envelope size, request timestamp.
  - **Secret (encrypted under group CEK):** every column of every row (the entire MessagePacked row body inside `ShadowRow.EncryptedRow`), including `UpdatedAt`, `IsDeleted`, foreign-key columns to other secret rows.
  - **At-rest secret (encrypted under per-DB VFS key):** every byte of every SQLite page; database filenames are stored in OPFS as opaque SAH handle bookkeeping (`sahpool-prf-vfs.ts:428-478` digest-protected) but are not themselves encrypted.
  - Determines P2 verdict: Covered (by stated policy) — the policy is "row payloads are secret; routing fields are not." Anything an attacker learns from routing fields (group sizes, sync frequency, relationship graph via recipient pubkeys) is acknowledged as a metadata trade-off.

## 9. Open Questions

- Should `RemoveMemberAsync` re-issue ShareTargets that carry valid `AdminSignature` + `GroupAdminEd25519PublicKey`, or is the current unsigned-rotation path intentional? (affects P6) — see remediation Phase 1.
- Should the OPFS-backed `IReceiveCursorStore` ship before or after the dual-DB rollout? Cursor persistence depends on OPFS schema; dual-DB introduces per-DB cursor namespacing. (affects P9)
- Is the long-term plan to add a per-CEK message-count cap (forcing rotation before the AES-GCM 96-bit-nonce birthday bound), or to rely on operational rotation cadence? (affects P11)
- Should the ShareTarget AdminSignature canonical extend to bind `tableName` or `Id`, so cross-table row replay is rejected at the AAD layer rather than only at the batch-signature layer? (affects P5)

## 10. Memory ↔ Code Reconciliation

- `project_dual_db_architecture.md` says "every device hosts exactly two CryptoSync DBs side by side" (locked 2026-04-28). Code as of commit 82ed639 has **no `DatabaseId`, `PublicDb`, or `PrivateDb` references** in `SqliteWasmBlazor.CryptoSync/`. → **Action: dual-DB is still aspirational.** This threat model describes the current single-CryptoSync-DB-per-device deployment. Re-run `/cryptosync-audit threat-model` once `twin-streams-flowing-codd.md` lands; the per-envelope `DatabaseId` field will introduce a new routing-metadata field that needs §8 classification.
- `project_relay_design.md` says "Persist `_lastCursor` to OPFS" is a TODO. Code now has the `IReceiveCursorStore` seam (`Services/IReceiveCursorStore.cs`), but the only concrete impl is `InMemoryReceiveCursorStore`. → **Memory is partially out of date**: the seam exists, the OPFS impl does not. Update memory after the OPFS store ships.
- `project_relay_design.md` says GET auth window is "±300 s" — code at `delta-relay.php:36, 133` confirms `RECEIVE_WINDOW_SECONDS = 300`. Match.
- `project_invitation_flow.md` describes the bundle as carrying an Ed25519 signature over `(transportPub || GroupId || ExpiresAt.Ticks)`. Code at `InvitationRoundtripTests.cs:46-66` confirms the canonical format via `ContactInvitationService.BuildBundleCanonical`. Match.
- `feedback_architecture.md` says "Worker owns decrypt/apply/permission enforcement; C# is a thin bridge." Confirmed against `crypto-ops.ts:421-520` (permission resolution chain entirely in worker).
