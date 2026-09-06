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
  lifecycle, temp/backup replacement, rollback, and pool-level
  decrypt-to-plain key purge.
- `vfs-tamarin/vfs-cache-import-lifecycle.spthy` — PRF seed / JS key-cache
  expiry, `KeyCacheStrategy.NONE` one-shot consumption, manifest-MAC-verified
  unlock, lock-on-expiry, deferred manifest persistence, and whole-pool import
  wipe-after-validate (pool wipe only after full-source validation; invalid
  sources rejected with the pool untouched).

The models predate the `.zip` whole-pool format's replacement by `.dbs`. The
`plain_zip_*` rule and lemma names are kept as stable identifiers in verified
proofs — they model the plain whole-pool import, whatever its container.

## Running

Use the scripts; they carry the invocation details you would otherwise have to
rediscover (locale, heap cap, per-batch lemma proving). Needs `tamarin-prover`
1.12.0+ and `maude` on PATH, and a few GB of free RAM:

```sh
./docs/formal/verify.sh                 # all three theories — every lemma must report `verified`
./docs/formal/verify.sh vfs             # one theory
./docs/formal/mutation-check.sh         # anti-vacuity: break each mechanism, assert its lemma FALSIFIES
nohup ./docs/formal/run-all.sh &        # both, detached, with a live log to tail
```

A theory can also be proven directly, which is what the scripts do underneath:

```sh
tamarin-prover --prove docs/formal/vfs-tamarin/vfs.spthy
```

Every `lemma` clause reports `verified` when the model holds against a Dolev-Yao
attacker over the encrypted at-rest channel. `verify.sh` alone is not the whole
gate: a lemma that cannot fail when its mechanism is removed proves nothing, so
`mutation-check.sh` is what shows the models still bite.
