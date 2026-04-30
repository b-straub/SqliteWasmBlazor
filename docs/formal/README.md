# Formal Models

This directory holds machine-checked symbolic models for the security-sensitive
parts of SqliteWasmBlazor.

## Models

- `vfs-tamarin/vfs.spthy` - Tamarin model for
  `SqliteWasmBlazor/TypeScript/worker/vfs-prf`, the PRF-keyed OPFS SAHPool VFS.
- `cryptosync-tamarin/*.spthy` - layered Tamarin models for the CryptoSync
  invitation, group key distribution, delta data-plane, relay whitelist/cursor,
  and admin pin/purge flows.
- `formal-implementation-audit.md` - refinement checklist mapping each formal
  claim to implementation code and executable tests.

## Running

From the repository root:

```sh
tamarin-prover --prove docs/formal/vfs-tamarin/vfs.spthy
tamarin-prover --prove docs/formal/cryptosync-tamarin/01-invitation-control-plane.spthy
tamarin-prover --prove docs/formal/cryptosync-tamarin/02-group-key-distribution.spthy
tamarin-prover --prove docs/formal/cryptosync-tamarin/03-delta-data-plane.spthy
tamarin-prover --prove docs/formal/cryptosync-tamarin/04-relay-whitelist-cursor.spthy
tamarin-prover --prove docs/formal/cryptosync-tamarin/05-pin-purge-authority.spthy
```
