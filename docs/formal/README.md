# Formal Models

This directory holds machine-checked symbolic models for the security-sensitive
parts of SqliteWasmBlazor. Each `.spthy` is a self-contained Tamarin theory that
encodes the intended security properties as `lemma` clauses and is verifiable
with [Tamarin Prover](https://tamarin-prover.com/).

## Models

- `vfs-tamarin/vfs.spthy` — Tamarin model for the PRF-keyed OPFS SAHPool VFS
  (`src/Crypto/SqliteWasmBlazor.Crypto/TypeScript/worker/vfs-prf`): per-slot
  ChaCha20-Poly1305 AEAD, AAD binding `(dbPath, slotIndex)`, global-key
  registration, slot-0 verification probe, and one bounded current-to-next
  rekey.
- `vfs-tamarin/vfs-inplace-lifecycle.spthy` — operational wrapper around export
  and in-place conversion: source-shape preconditions, worker global-key
  lifecycle, temp/backup replacement, rollback, and disk-level
  decrypt-to-plain key purge.
- `vfs-tamarin/vfs-cache-import-lifecycle.spthy` — PRF seed / JS key-cache
  expiry, `KeyCacheStrategy.NONE` one-shot consumption, manifest-MAC-verified
  unlock, lock-on-expiry, deferred manifest persistence, and whole-disk import
  preflight.

## Running

From the repository root:

```sh
tamarin-prover --prove docs/formal/vfs-tamarin/vfs.spthy
tamarin-prover --prove docs/formal/vfs-tamarin/vfs-inplace-lifecycle.spthy
tamarin-prover --prove docs/formal/vfs-tamarin/vfs-cache-import-lifecycle.spthy
```

Each invocation reports `verified` for every `lemma` clause when the model
holds against a Dolev-Yao attacker over the encrypted at-rest channel.
