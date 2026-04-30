# CryptoSync Assurance Matrix

_Current checkpoint: 2026-04-29._

This matrix ties the CryptoSync property catalog to three evidence layers:

- **Formal:** Tamarin lemmas under `docs/formal`.
- **Executable:** unit, integration, relay, TypeScript, and Playwright tests.
- **Residual:** the remaining policy assumption, implementation gap, or operational
  scope boundary.

The generated `property-catalog.md` is the source list of properties. This file
is the current cross-reference and reflects the whitelist-authenticated relay
rewrite and the live relay test run.

## Verification Snapshot

Last local verification pass:

| Surface | Command | Result |
|---|---|---|
| VFS + CryptoSync Tamarin | `tamarin-prover --prove ...` for `vfs.spthy` plus `01` through `04` CryptoSync theories | Passed |
| Direct PHP relay harness | `XDEBUG_MODE=off php DeltaRelay/tests/relay-integration.php` | 15 passed |
| C# relay wire/client tests | `dotnet test ... --filter "FullyQualifiedName~HttpSyncTransportTests\|FullyQualifiedName~WhitelistPushServiceTests"` | 18 passed |
| Herd/Valet LiveRelay | `RUN_LIVE_RELAY_TESTS=1 dotnet test ... --filter "Category=LiveRelay"` | 13 passed |
| Playwright browser surface | `dotnet test SqliteWasmBlazor.Tests/SqliteWasmBlazor.Tests.csproj` | 60 passed |

## Coverage Legend

| Mark | Meaning |
|---|---|
| Strong | Covered by formal model plus executable tests, or by two independent executable layers. |
| Policy | Covered only under a stated threat-model policy or product decision. |
| Partial | Meaningful coverage exists, but an important executable or formal gap remains. |
| Operational | Depends on deployment controls outside this repo's normal test harness. |

## Property Traceability

| ID | Property | Formal evidence | Executable evidence | Current status and residual |
|---|---|---|---|---|
| P1 | Storage backend never observes plaintext payload bytes | `VfsPrfSlotEncryption.encrypted_slot_secrecy_unless_plain_exported`; `CryptoSyncDeltaDataPlane.delta_row_secrecy` | `PlaintextLeakProperty.cs`; `InvitationRoundtripTests.cs`; `PrfVfsEnvelopeTests.cs`; `vfs-prf/__tests__/envelope.test.ts`; direct PHP relay stores opaque envelopes only | Strong. Relay and VFS see ciphertext; metadata leakage is handled by P2. |
| P2 | Secret metadata encrypted under same/stronger key, AAD-bound | `wrong_aad_rows_not_accepted`; VFS AAD lemmas via `encrypted_read_authenticity` | `V2CryptoHeaderTests.cs`; `vfs-prf/__tests__/envelope.test.ts`; Playwright browser smoke tests | Policy. Routing metadata is intentionally visible per threat model; row bodies and VFS slots are protected. |
| P3 | IND-CPA at storage layer | Symbolic secrecy lemmas model ciphertext opacity, not computational IND-CPA | `aes-gcm-ind-cpa.test.ts`; `crypto-core/tests/aesGcm.test.ts`; `vfs-prf/__tests__/envelope.test.ts` | Strong at implementation-test level. Formal layer idealizes AEAD. |
| P4 | Modification of ciphertext or metadata detected on read | `encrypted_read_authenticity`; `accepted_delta_has_sender_source`; `wrong_aad_rows_not_accepted`; invitation/group source lemmas | `DeclarationSignerTests.cs`; `SignedShareTargetTests.cs`; `DeltaEnvelopeTests.cs`; `PrfVfsEnvelopeTests.cs`; `vfs-prf/__tests__/envelope.test.ts` | Strong. Dumb relay may store tampered bytes; receiver rejects them. |
| P5 | AAD binds ciphertext to logical identifier, version, owning context | `wrong_aad_rows_not_accepted`; VFS `encrypted_read_authenticity`; VFS rekey lemmas | `V2CryptoHeaderTests.cs`; VFS cross-DB/slot tests; `rekey.test.ts`; `PrfVfsEnvelopeTests.cs` | Strong for current design, with defense-in-depth note: per-row AAD binds `groupContext:keyVersion`; table/id relocation relies on signed batch/envelope context. |
| P6 | Only clients with right subkey produce accepted ciphertexts | `accepted_delta_requires_authorized_sender`; `accepted_sharetarget_has_admin_source`; `accepted_sharetarget_installs_member_cek`; relay `post_requires_active_status` | `GroupServiceTests.cs`; `SignedShareTargetTests.cs`; `HttpSyncTransportLiveRelayTests.cs`; `relay-integration.php`; `WhitelistPushServiceTests.cs` | Strong for current relay + crypto-layer model. Availability abuse is bounded to whitelisted senders plus operator rate limits. |
| P7 | Previously valid ciphertext cannot be substituted for newer one in same logical slot | `receive_cursor_never_rolls_back`; relay cursor/whitelist monotonic lemmas | `HttpSyncTransportTests.cs`; `EfReceiveCursorStoreTests.cs`; `SyncEngineTests.cs`; Playwright delta conflict cases | Policy. DeltaWins means an old valid envelope at a newer relay cursor can overwrite by policy. The guarantee is monotonic arrival order, not semantic freshness by row timestamp. |
| P8 | Signed requests include nonce or monotonic counter; replays rejected | Relay access/auth lemmas model signed access and cursor monotonicity | `PostEnvelope_StaleTimestamp_Returns401`; forged sender/receiver checks in `relay-integration.php`; `HttpSyncTransportTests.cs` | Policy. Requests are timestamp-windowed, not nonce-stored. Replay within the accepted window is allowed by threat model because payloads remain ciphertext. |
| P9 | Sync rounds advance monotonically; rollback to earlier snapshot detectable | `receive_cursor_never_rolls_back`; `whitelist_version_does_not_roll_back` | `HttpSyncTransportTests.cs`; `EfReceiveCursorStoreTests.cs`; `SyncEngineTests.cs`; LiveRelay cursor/reseed tests | Strong for relay cursor and persisted receive cursor. Whole-OPFS rollback remains out of scope. |
| P10 | Each derived key used in one context via domain-separated KDF | Group and invitation models separate signing, channel, and CEK roles; VFS model separates slot keys | `crypto-core/tests/keyDerivation.test.ts`; `keyWrapping.test.ts`; `V2CryptoHeaderTests.cs`; `PrfVfsEnvelopeTests.cs` | Strong. The design and runtime paths are covered; an explicit "distinct context strings produce distinct derived keys" property test would be optional defense in depth. |
| P11 | Nonces unique across clients and keys | `VfsPrfSlotEncryption.nonce_never_reused` | `nonce-uniqueness.test.ts`; `aes-gcm-ind-cpa.test.ts`; `crypto-core` AEAD tests | Strong for regression detection. Long-lived random 96-bit nonce birthday limits remain an operational scale/rotation question. |
| P12 | Key rotation produces fresh unrelated key | `rotated_cek_is_fresh`; `revoked_member_gets_no_rotated_sharetarget`; VFS rekey soundness lemmas | `GroupTransferServiceTests.cs`; `GroupServiceTests.cs`; `rekey.test.ts`; `PrfVfsEnvelopeTests.cs` | Strong. Old ciphertext remains decryptable to old key holders; forward secrecy starts after rotation. |
| P13 | Concurrent writers produce deterministic conflict state | Relay cursor total order in `receive_cursor_never_rolls_back` | Playwright `ExportImport_DeltaConflict*`; `TwoActorFixtureTests.cs`; `SyncEngineTests.cs` | Policy. Deterministic under DeltaWins by relay cursor order; no column-level merge guarantee yet. |
| P14 | Conflict resolution does not merge plaintext from unauthorized inputs | Not modeled as receiver-read secrecy under the current full-snapshot policy; delta model covers sender authenticity, AAD rejection, and row secrecy from the relay | `SharingServiceTests.cs`; browser `CryptoSync_PermissionEnforcement`; sender-denied mutation checks in `PermissionEnforcementTest.cs`; Playwright delta conflict cases | Policy. Receiver `CanRead` is not a sync-layer materialization gate today. Clients with the relevant CEK may carry and apply the full group snapshot; current import enforces sender mutation authorization. Sender-denied mutation behavior is covered by browser tests. |
| P15 | Stale client write does not silently overwrite newer write | Same evidence boundary as P13/P7 | Playwright delta conflict cases; `SyncEngineTests.cs` | Policy. Stale writes can overwrite if they arrive later; this is explicitly DeltaWins. A future merge policy must replace this row. |
| P16 | Compromised device reads only own history, not future content | `revoked_member_gets_no_rotated_sharetarget`; `group_cek_secret`; relay revoke/grace lemmas | `LeaveServiceTests.cs`; `GroupServiceTests.cs`; `GroupTransferServiceTests.cs`; LiveRelay revoke tests | Policy/strong after rotation. Revocation declaration alone is not forward secrecy; admin key rotation is required. |
| P17 | Device loss recoverable without re-encrypting all historical data | Invitation lemmas: channel secrecy, admin bundle source, contact response source | `InvitationRoundtripTests.cs`; `ContactInvitationServiceTests.cs`; `ContactServiceTests.cs`; browser smoke coverage | Policy. Re-invited devices get future/current ShareTargets, not guaranteed historical backfill. |
| P18 | Revocation effective at protocol level, not just server ACL | Group revocation lemma plus relay revoke lemmas | `DeclarationSignerTests.cs`; `LeaveServiceTests.cs`; `GroupServiceTests.cs`; LiveRelay revoke/grace tests; direct PHP relay revoke tests | Policy/strong when paired with key rotation. Automatic rotation-on-declaration is not yet enforced. |

## Layer Map

| Layer | Formal model | Main executable tests | What it proves or checks |
|---|---|---|---|
| VFS at-rest encryption | `docs/formal/vfs-tamarin/vfs.spthy` | `vfs-prf/__tests__/envelope.test.ts`; `rekey.test.ts`; `PrfVfsEnvelopeTests.cs`; Playwright TestApp | Slot secrecy, AAD binding, rekey soundness, nonce uniqueness, tamper rejection. |
| Invitation control plane | `01-invitation-control-plane.spthy` | `InvitationRoundtripTests.cs`; `ContactInvitationServiceTests.cs`; `ContactServiceTests.cs` | Admin-signed bundle provenance, invitation channel secrecy, contact-signed response provenance. |
| Group key distribution | `02-group-key-distribution.spthy` | `GroupServiceTests.cs`; `SignedShareTargetTests.cs`; `GroupTransferServiceTests.cs` | CEK secrecy, admin-signed ShareTargets, install provenance, revocation rotation. |
| Delta data plane | `03-delta-data-plane.spthy` | `DeltaEnvelopeTests.cs`; `PlaintextLeakProperty.cs`; `DeclarationSignerTests.cs`; `SyncEngineTests.cs`; Playwright delta cases | Row secrecy, sender authenticity, authorization, AAD rejection, import/export behavior. |
| Relay whitelist/cursor | `04-relay-whitelist-cursor.spthy` | `relay-integration.php`; `HttpSyncTransportTests.cs`; `HttpSyncTransportLiveRelayTests.cs`; `WhitelistPushServiceTests.cs` | Admin whitelist authority, active POST, revoked grace GET, expiry denial, cursor monotonicity. |
| Relay pin/purge | `05-pin-purge-authority.spthy` | `relay-integration.php`; `HttpSyncTransportLiveRelayTests.cs` | Deployment-admin-only pinned reseed purge, deltapin/deltapost signature separation, monotonic purge epoch. |
| Browser integration | No browser-specific formal model | `SqliteWasmBlazor.Tests` Playwright suite | End-to-end TestApp workflows, WASM/OPFS bridge, sub-path/CSP behavior, broad smoke coverage. |

## Current Gaps

These are the remaining "not complete in the strict sense" items:

1. **Concurrent relay stress.** Current relay tests prove sequential behavior
   and transaction semantics, not high-contention request races.
2. **Production web-server controls.** TLS, server headers, PHP ini, rate limits,
   backup/restore, and filesystem ownership are deployment checks, not covered by
   the PHP built-in-server harness.
3. **Replay-within-window.** Accepted by policy today. A per-pubkey nonce or
   monotonic request table would be needed to make P8 a hard replay-rejection
   guarantee.
4. **Automatic revocation rotation.** Revocation is protocol-effective only once
   the admin rotates group keys. The tests prove the pieces; they do not prove an
   automatic workflow because the workflow is not implemented as automatic.
5. **Whole-OPFS rollback.** Explicitly out of scope. Cursor monotonicity is only
   as durable as the local state the browser gives back to the app.
6. **Crypto primitive correctness.** Trusted to sodium, WebCrypto, noble, and
   crypto-core tests; Tamarin models ideal primitives.

## Completeness Statement

The current suite gives strong layered assurance for the CryptoSync protocol
under the threat model's stated policies. It is complete enough for the relay,
transport, invitation, group key, delta-envelope, VFS, and browser smoke
surfaces to move together with confidence.

It is not a full proof of every operational behavior of a production deployment.
The main remaining gaps are operational rather than local CryptoSync test gaps:
concurrency stress, deployment hardening checks, replay policy tightening,
automatic revocation rotation, and whole-OPFS rollback outside the browser's
returned state. If a future product layer introduces receiver-side read
materialization or filtering, P14 must be reopened with a browser-backed
xUnit/Playwright test that executes the TypeScript sqlite-wasm worker path.
