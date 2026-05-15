# Security Documentation

This folder collects the security-relevant documentation for SqliteWasmBlazor.

## Contents

| File | Description |
|---|---|
| [threat-model.md](threat-model.md) | Attacker model, in-scope and out-of-scope threats, channel-by-channel defenses, cryptographic primitives summary, and known limitations. Scope: all three planes. |
| [relay-whitelist-design.md](relay-whitelist-design.md) | Design rationale for the DeltaRelay whitelist-broadcast model (Plane 3): authenticated POST, unbounded broadcast read, admin-only purge. |
| [`../formal/`](../formal/README.md) | Machine-checked Tamarin models — Plane-2 encryption VFS (3 theories) + Plane-3 multi-device sync (5 theories). |
| [`../crypto-vfs.md`](../crypto-vfs.md) | Implementation reference for the PRF-keyed encryption VFS (Plane 2): page-level ChaCha20-Poly1305, AAD layout, code-reference table. |
| [`../cryptosync-schema.md`](../cryptosync-schema.md) | Schema reference for `CryptoSyncContextBase` (Plane 3): every shadow table, registry, permission row, and the wire-format types they back. |

## Assurance summary

### Plane 2 — encryption VFS

| Property | Evidence |
|---|---|
| At-rest confidentiality of SQLite pages | ChaCha20-Poly1305 per 4 096-byte slot; AAD binds `(versionTag, dbPath, slotIndex)`. Lemma `encrypted_slot_secrecy_unless_plain_exported` in `vfs.spthy`. |
| Tamper detection on OPFS contents | AEAD authentication failure surfaces as a read error. Lemma `legacy_ciphertexts_not_read_as_v1` in `vfs.spthy`. |
| Cross-database page swap rejection | `dbPath` in AAD. Lemma `encrypted_read_authenticity` in `vfs.spthy`. |
| Cross-slot page swap rejection | `slotIndex` in AAD. Same lemma `encrypted_read_authenticity`. |
| Wrong-key unlock rejection | Slot-0 AEAD probe + manifest MAC verification before unlock. Lemmas `unlock_requires_seed_cache` + `accepted_unlock_requires_manifest_mac_verified` in `vfs-cache-import-lifecycle.spthy`. |
| Preflight before destructive whole-disk import | Mistargeted `.eds` / wrong-shape `.zip` rejected before the current disk is touched. Lemmas `plain_zip_import_accept_requires_preflight` + `cipher_import_accept_requires_preflight`. |
| Bounded one-shot key rotation | Single current-to-next rotation per disk; legacy ciphertext rejected after rotation. Lemma `rekey_encrypted_to_encrypted_sound` in `vfs.spthy`. |
| Nonce uniqueness | Lemma `nonce_never_reused` in `vfs.spthy`. |

### Plane 3 — multi-device sync

| Property | Evidence |
|---|---|
| Relay sees only opaque ciphertext | Per-row AES-GCM CEK is per-group, distributed via admin-signed ShareTarget; AAD binds `groupContext:keyVersion`. Lemmas in `03-delta-data-plane.spthy`. |
| Sender authenticity on every envelope | Outer Ed25519 signature + per-group batch Ed25519 signature. Lemma `envelope_outer_signature_authenticity` in `03-delta-data-plane.spthy`. |
| Group-key rotation effective after revocation | One bounded current-to-next rotation modeled. Lemmas in `02-group-key-distribution.spthy`. |
| Whitelist-bound relay POST authorization | Ed25519 signature against deployment-admin-signed whitelist; revoked-write authorization invalidated immediately. Lemmas in `04-relay-whitelist-cursor.spthy`. |
| Monotonic receive cursor | Per-recipient cursor advances only forward, replay outside ±300 s window rejected. Lemma `cursor_monotonic` in `04-relay-whitelist-cursor.spthy`. |
| Admin-only pin/purge authority | Deployment admin Ed25519 key authorizes pinned-reseed; purge epoch monotonic. Lemmas in `05-pin-purge-authority.spthy`. |
| Invitation transport-key whitelist lifecycle | Admin-signed bundle, contact-signed response identity, transport-key whitelist install + revoke. Lemmas in `01-invitation-control-plane.spthy`. |

### Cross-plane

| Property | Evidence |
|---|---|
| Memory hygiene for secret buffers | `CryptographicOperations.ZeroMemory` on every C# secret-bearing buffer; `clearBytes` helper on every JS bridge boundary; `MemoryView.slice()` at every C#→JS bridge. |

The plain (Plane 1) layer makes no application-layer confidentiality claim
beyond what the host OS and user agent provide for OPFS files. Plane 2 and
Plane 3 are additive: Plane 2 adds at-rest confidentiality, Plane 3 adds
authenticated multi-device data flow.

## Verifying the formal models

All eight Tamarin theories (3 for Plane 2, 5 for Plane 3) are
self-contained and verifiable with the public
[Tamarin Prover](https://tamarin-prover.com/) toolchain. From the
repository root:

```sh
# Plane 2 — encryption VFS
tamarin-prover --prove docs/formal/vfs-tamarin/vfs.spthy
tamarin-prover --prove docs/formal/vfs-tamarin/vfs-inplace-lifecycle.spthy
tamarin-prover --prove docs/formal/vfs-tamarin/vfs-cache-import-lifecycle.spthy

# Plane 3 — multi-device sync
tamarin-prover --prove docs/formal/cryptosync-tamarin/01-invitation-control-plane.spthy
tamarin-prover --prove docs/formal/cryptosync-tamarin/02-group-key-distribution.spthy
tamarin-prover --prove docs/formal/cryptosync-tamarin/03-delta-data-plane.spthy
tamarin-prover --prove docs/formal/cryptosync-tamarin/04-relay-whitelist-cursor.spthy
tamarin-prover --prove docs/formal/cryptosync-tamarin/05-pin-purge-authority.spthy
```

Each invocation reports `verified` for every `lemma` clause when the model
holds against a Dolev-Yao attacker over the modeled channel.

## Reporting vulnerabilities

Please report security issues through GitHub's private vulnerability
reporting rather than as a public issue:

→ <https://github.com/b-straub/SqliteWasmBlazor/security/advisories/new>

The maintainer is notified privately; the report stays embargoed until
a fix is published. If the link 404s the feature has not been enabled
on the repository yet — open a regular issue asking for it to be turned
on (without disclosing details).
