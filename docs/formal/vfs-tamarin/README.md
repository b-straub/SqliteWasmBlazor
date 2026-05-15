# PRF VFS Tamarin Model

This folder contains Tamarin models for the PRF-keyed VFS implementation in
`src/Crypto/SqliteWasmBlazor.Crypto/TypeScript/worker/vfs-prf`, its in-place
conversion wrapper, and the disk-level PRF cache/import lifecycle.

## Files

- `vfs.spthy` models the per-slot AEAD, global-key registration, verification, and
  rekey primitive.
- `vfs-inplace-lifecycle.spthy` models the operational wrapper around export
  and in-place conversion: source-shape preconditions, worker global-key
  lifecycle, temp/backup replacement, rollback, and disk-level
  decrypt-to-plain key purge.
- `vfs-cache-import-lifecycle.spthy` models PRF seed / JS key-cache expiry,
  `KeyCacheStrategy.NONE` one-shot consumption, manifest-MAC-verified unlock,
  lock-on-expiry, deferred manifest persistence, and whole-disk import preflight.

## Scope

The model covers the encrypted at-rest channel:

- worker-wide VFS global-key registration,
- page AAD binding to version, path, and slot index,
- encrypted xWrite/xRead over a public attacker-controlled disk channel,
- slot-0 `verifyEncryptionKey` soundness,
- one bounded current-to-next key rotation,
- plain-to-encrypted, encrypted-to-plain, encrypted-to-encrypted, and
  plain-to-plain rekey events,
- legacy/cross-version ciphertext rejection,
- symbolic nonce freshness.
- PRF cache expiry clearing the worker global key and preserving the hint,
- `KeyCacheStrategy.NONE` consuming the C# seed entry on first key use
  (the JS-side bundle is modelled as session-lifetime under every strategy —
  see `vfs-cache-import-lifecycle.spthy` for the rule shape and the
  `crypto-vfs.md` "NONE" note for the runtime rationale),
- manifest MAC verification before unlock acceptance,
- whole-disk plain-ZIP and cipher-envelope import acceptance/rejection by
  current disk state, per-file content kind, and pre-destructive preflight.

Plain VFS mode and rekey-to-plain are represented as events, not confidentiality
claims. The implementation returns plain bytes to the trusted caller in those
modes; the at-rest attacker proof is about encrypted disk material.

## Proved Lemmas

Run:

```sh
tamarin-prover --prove docs/formal/vfs-tamarin/vfs.spthy
tamarin-prover --prove docs/formal/vfs-tamarin/vfs-inplace-lifecycle.spthy
tamarin-prover --prove docs/formal/vfs-tamarin/vfs-cache-import-lifecycle.spthy
```

Expected `vfs.spthy` summary:

- `key_secrecy`
- `encrypted_slot_secrecy_unless_plain_exported`
- `encrypted_read_authenticity`
- `verify_key_match_sound`
- `rekey_encrypted_to_plain_sound`
- `rekey_encrypted_to_encrypted_sound`
- `legacy_ciphertexts_not_read_as_v1`
- `nonce_never_reused`

Expected `vfs-inplace-lifecycle.spthy` summary:

- `key_install_requires_empty_global_key`
- `export_encrypt_requires_plain_without_global_key`
- `encrypt_in_place_requires_plain_without_global_key`
- `export_plain_requires_encrypted_global_key`
- `export_rekey_requires_encrypted_global_key`
- `decrypt_in_place_requires_encrypted_global_key`
- `decrypt_success_keeps_global_key_until_disk_leave`
- `decrypt_failure_keeps_global_key`
- `leave_encrypted_clears_global_key`
- `replacement_failure_restores_original`
- `encrypt_failure_restores_plain_original`
- `decrypt_failure_restores_encrypted_original`
- `encrypt_success_poststate`
- `decrypt_success_poststate`

Expected `vfs-cache-import-lifecycle.spthy` summary:

- `hint_write_is_after_disk_encryption`
- `unlock_requires_seed_cache`
- `accepted_unlock_requires_manifest_mac_verified`
- `rejected_unlock_clears_global_key`
- `none_seed_is_consumed_exactly_once`
- `cache_expiry_locks_and_clears_global_key`
- `cache_expiry_preserves_hint`
- `plain_zip_import_accept_requires_preflight`
- `unlocked_plain_zip_import_stays_encrypted`
- `locked_plain_zip_import_breaks_to_plain`
- `cipher_import_accept_requires_preflight`
- `guided_cipher_import_from_plain_ends_unlocked`
- `guided_cipher_import_from_locked_ends_unlocked`
- `plain_disk_rejects_cipher_import`

All are `verified` with Tamarin 1.12.0 in the local toolchain used when this was
written.
