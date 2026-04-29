# PRF VFS Tamarin Model

This folder contains the Tamarin model for the PRF-keyed VFS implementation in
`SqliteWasmBlazor/TypeScript/worker/vfs-prf`.

## Scope

The model covers the encrypted at-rest channel:

- per-path VFS key registration,
- page AAD binding to version, path, and slot index,
- encrypted xWrite/xRead over a public attacker-controlled disk channel,
- slot-0 `verifyEncryptionKey` soundness,
- one bounded current-to-next key rotation,
- plain-to-encrypted, encrypted-to-plain, encrypted-to-encrypted, and
  plain-to-plain rekey events,
- legacy/cross-version ciphertext rejection,
- symbolic nonce freshness.

Plain VFS mode and rekey-to-plain are represented as events, not confidentiality
claims. The implementation returns plain bytes to the trusted caller in those
modes; the at-rest attacker proof is about encrypted disk material.

## Proved Lemmas

Run:

```sh
tamarin-prover --prove docs/formal/vfs-tamarin/vfs.spthy
```

Expected summary:

- `key_secrecy`
- `encrypted_slot_secrecy_unless_plain_exported`
- `encrypted_read_authenticity`
- `verify_key_match_sound`
- `rekey_encrypted_to_plain_sound`
- `rekey_encrypted_to_encrypted_sound`
- `legacy_ciphertexts_not_read_as_v1`
- `nonce_never_reused`

All are `verified` with Tamarin 1.12.0 in the local toolchain used when this was
written.

