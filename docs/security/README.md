# Security Documentation

This folder collects the security-relevant documentation for SqliteWasmBlazor.

## Contents

| File | Description |
|---|---|
| [threat-model.md](threat-model.md) | Attacker model, in-scope and out-of-scope threats, channel-by-channel defenses, cryptographic primitives summary, and known limitations. Scope: plain SQLite WASM engine + PRF-keyed encryption VFS. |
| [`../formal/`](../formal/README.md) | Machine-checked Tamarin models for the encryption VFS — per-slot AEAD soundness, in-place lifecycle, and key-cache / manifest unlock lemmas. |
| [`../crypto-vfs.md`](../crypto-vfs.md) | Implementation reference for the PRF-keyed encryption VFS: page-level ChaCha20-Poly1305, AAD layout, code-reference table. |

## Assurance summary

| Property | Layer | Evidence |
|---|---|---|
| At-rest confidentiality of SQLite pages | Encryption VFS | ChaCha20-Poly1305 per 4 096-byte slot; AAD binds `(versionTag, dbPath, slotIndex)`. Formal model: `docs/formal/vfs-tamarin/vfs.spthy`. |
| Tamper detection on OPFS contents | Encryption VFS | AEAD authentication failure surfaces as a read error. Lemma `legacy_or_cross_version_ciphertext_rejected` in `vfs.spthy`. |
| Cross-database page swap rejection | Encryption VFS | `dbPath` in AAD. Lemma `cross_db_swap_rejected`. |
| Cross-slot page swap rejection | Encryption VFS | `slotIndex` in AAD. Lemma `cross_slot_swap_rejected`. |
| Wrong-key unlock rejection | Encryption VFS lifecycle | Slot-0 AEAD probe + manifest MAC verification before unlock acceptance. Modeled in `vfs-cache-import-lifecycle.spthy`. |
| Preflight before destructive whole-disk import | Encryption VFS lifecycle | Mistargeted `.eds` or wrong-shape `.zip` rejected before the current disk is touched. Modeled in `vfs-cache-import-lifecycle.spthy`. |
| Bounded one-shot key rotation | Encryption VFS | Single current-to-next rotation per disk; legacy ciphertext rejected after rotation. Modeled in `vfs.spthy`. |
| Memory hygiene for secret buffers | C# / TypeScript | `CryptographicOperations.ZeroMemory` on every C# secret-bearing buffer; `clearBytes` helper on every JS bridge boundary. |

The plain (Plane 1) layer makes no application-layer confidentiality claim
beyond what the host OS and user agent provide for OPFS files. The whole
catalog above applies to the Plane-2 encryption VFS.

## Verifying the formal models

The three Tamarin theories under `docs/formal/vfs-tamarin/` are
self-contained and verifiable with the public
[Tamarin Prover](https://tamarin-prover.com/) toolchain. From the
repository root:

```sh
tamarin-prover --prove docs/formal/vfs-tamarin/vfs.spthy
tamarin-prover --prove docs/formal/vfs-tamarin/vfs-inplace-lifecycle.spthy
tamarin-prover --prove docs/formal/vfs-tamarin/vfs-cache-import-lifecycle.spthy
```

Each invocation reports `verified` for every `lemma` clause when the model
holds against a Dolev-Yao attacker over the encrypted at-rest channel.

## Reporting vulnerabilities

Please open a private security advisory on the project's GitHub repository
rather than a public issue. See the repository root `README.md` for
contributor contact details.
