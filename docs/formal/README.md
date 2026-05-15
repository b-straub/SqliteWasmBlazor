# Formal Models

This directory holds machine-checked symbolic models for the security-sensitive
parts of SqliteWasmBlazor. Each `.spthy` is a self-contained Tamarin theory that
encodes the intended security properties as `lemma` clauses and is verifiable
with [Tamarin Prover](https://tamarin-prover.com/).

## Models

### `vfs-tamarin/` — encryption VFS (Plane 2)

- `vfs.spthy` — Tamarin model for the PRF-keyed OPFS SAHPool VFS
  (`src/Crypto/SqliteWasmBlazor.Crypto/TypeScript/worker/vfs-prf`): per-slot
  ChaCha20-Poly1305 AEAD, AAD binding `(dbPath, slotIndex)`, global-key
  registration, slot-0 verification probe, and one bounded current-to-next
  rekey.
- `vfs-inplace-lifecycle.spthy` — operational wrapper around export and
  in-place conversion: source-shape preconditions, worker global-key
  lifecycle, temp/backup replacement, rollback, and disk-level
  decrypt-to-plain key purge.
- `vfs-cache-import-lifecycle.spthy` — PRF seed / JS key-cache expiry,
  `KeyCacheStrategy.NONE` one-shot consumption, manifest-MAC-verified
  unlock, lock-on-expiry, deferred manifest persistence, and whole-disk
  import preflight.

### `cryptosync-tamarin/` — multi-device sync (Plane 3)

- `01-invitation-control-plane.spthy` — admin-signed invitation bundle,
  invitation-response channel, contact-signed response identity, invitation
  transport-key whitelist lifecycle.
- `02-group-key-distribution.spthy` — admin-signed ShareTarget credentials,
  CEK distribution, one bounded revocation rotation.
- `03-delta-data-plane.spthy` — DeltaEnvelope outer signature, per-group
  batch signature, row AEAD with `groupContext:keyVersion` AAD,
  receiver-local table classification, active-principal authorization,
  receiver acceptance provenance.
- `04-relay-whitelist-cursor.spthy` — whitelist update authority,
  sender/receiver relay authorization, revoked-read grace, monotonic
  cursor acceptance.
- `05-pin-purge-authority.spthy` — deployment-admin-only pinned-reseed
  purge, deltapin/deltapost canonical separation, monotonic purge epoch.

## Running

From the repository root:

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
