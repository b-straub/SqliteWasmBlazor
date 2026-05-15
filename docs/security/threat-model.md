# SqliteWasmBlazor Threat Model

Scope: the public SqliteWasmBlazor surface — the plain SQLite WASM engine
(`src/Base/SqliteWasmBlazor`) and the optional PRF-keyed encryption VFS
(`src/Crypto/SqliteWasmBlazor.Crypto`). Multi-device encrypted sync is a
separate downstream concern and is out of scope here.

## 1. System sketch

SqliteWasmBlazor is a Blazor WebAssembly application running entirely in the
browser. There is no server-side database; the only persistent backing store
is the user agent's Origin Private File System (OPFS) directory.

The runtime topology is:

- **Single SQLite engine in a Web Worker.** The .NET side carries an 8 KB
  stub that satisfies SQLitePCLRaw P/Invoke. Every SQL call from .NET is
  forwarded through `SqliteWasmWorkerBridge` to the worker, which runs the
  real `sqlite3.wasm` against an OPFS SAHPool VFS.
- **Plain mode (Plane 1).** Pages land in OPFS unencrypted. Confidentiality
  on disk is whatever the host OS and user agent provide.
- **Encrypted mode (Plane 2).** A 32-byte global key, derived from a passkey
  via the WebAuthn PRF extension and bridged through `PrfService`, is
  registered with the worker. Every 4 096-byte logical SQLite slot is then
  sealed with ChaCha20-Poly1305 before it reaches OPFS, and decrypted on
  read. The physical slot layout is `[ciphertext(4096) | nonce(12) | tag(16)]`,
  and AAD binds `(versionTag, dbPath, slotIndex)`.

Plane 2 is opt-in and composable: a host that never registers a key sees
byte-for-byte vendor SAHPool behavior.

## 2. Actors

| Actor | Trust | Capabilities |
|---|---|---|
| Local device user | Trusted | Holds the primary authenticator (passkey + WebAuthn PRF). All key material flows from this authenticator. |
| Operating system / user agent | Trusted | Owns the OPFS directory, schedules the worker, isolates origins. |
| Local-disk attacker (cold) | Hostile | Reads OPFS files offline — stolen laptop, forensic recovery, cloud-backup capture. |
| Network attacker | Active Dolev-Yao | Out of scope for the embedded engine. Relevant only if the host application transports SqliteWasmBlazor's bulk-export / import files; those carry no application-layer authenticity by default. |
| Same-origin script (XSS, dependency compromise) | Hostile | Out of scope; addressed by Content Security Policy and code review at higher layers. |
| Endpoint malware, live-process memory dump | Out of scope | Addressed by OS hygiene and hardware. |

## 3. In-scope threats

1. **Local-disk read of OPFS files.** Stolen device, suspended laptop,
   forensic recovery, cloud backups of the browser profile. Plane 1 makes no
   confidentiality claim against this. Plane 2 defends per-slot under
   ChaCha20-Poly1305.
2. **Tampering with OPFS contents.** Byte flips in ciphertext on disk. Plane 2
   detects via AEAD authentication failure.
3. **Cross-database page swapping.** Moving an encrypted page from DB A to
   DB B in the same OPFS pool. Plane 2 binds `dbPath` into AAD, so the swap
   fails to decrypt.
4. **Cross-slot page swapping.** Moving an encrypted page from slot _i_ to
   slot _j_ within the same DB. Plane 2 binds `slotIndex` into AAD.
5. **Whole-disk rollback to an earlier snapshot.** Replacing the entire OPFS
   tree with a previous capture of the same encrypted state. **Not defended
   at this layer** — the AEAD authenticates each page but not the disk's
   monotonic history. Use a higher-layer integrity scheme (e.g., a
   server-signed manifest in a multi-device deployment) if this matters.
6. **Wrong-key unlock attempt.** A different passkey is presented for an
   existing encrypted disk. Plane 2 rejects via a slot-0 AEAD probe
   (`verifyEncryptionKey`) and a manifest MAC bound to the credential.
7. **Mistargeted import.** A `.eds` envelope encrypted for a different
   recipient is imported. The import path preflights the envelope's MAC and
   page-shape before any destructive operation on the current disk.

## 4. Out of scope

- Confidentiality / authenticity of data **in transit** between devices.
  SqliteWasmBlazor ships bulk-export and import primitives, but the
  transport layer is left to consumer applications.
- Live-process memory dumps. The crypto primitives wipe transient buffers
  with `CryptographicOperations.ZeroMemory` and worker-side `clearBytes`
  helpers, but a live attacker with full process access can still observe
  derived keys.
- Same-origin script compromise (XSS, dependency takeover). This is a
  general-purpose offline-first application substrate; the host application
  is responsible for CSP, code review, and dependency hygiene.
- Network-layer eavesdropping. Plane 1+2 do not assume an in-browser network
  channel and make no claim about one.

## 5. Channels and defenses

| Channel | Wire format | Authenticity / confidentiality |
|---|---|---|
| .NET ↔ Worker (intra-page) | `postMessage` request/response envelope | Same-origin, structured cloning. No application-layer crypto. |
| Worker ↔ OPFS (Plane 1) | Raw SQLite pages | None — relies on OS file permissions. |
| Worker ↔ OPFS (Plane 2) | `[ciphertext(4096) \| nonce(12) \| tag(16)]` per slot | ChaCha20-Poly1305 AEAD with AAD `"prf-vfs-v1\|" + dbPath + "\|" + slotIndex_LE32`. Slot-0 probe gates unlock. Manifest MAC binds to credential. |
| Whole-disk export (`.eds`) | MessagePacked `EncryptedDiskEnvelope` | ECIES-wrapped slot key under recipient X25519 pubkey; per-slot AEAD; envelope-level MAC over `(credentialIdHint, slot headers)`. |
| Whole-disk export (`.zip` plain) | ZIP of `.db` files | None at this layer. Confidentiality is on Plane 1 storage. |

## 6. Cryptographic primitives

| Role | Primitive | Standard |
|---|---|---|
| Page AEAD | ChaCha20-Poly1305 | RFC 8439 |
| Nonce source | `crypto.getRandomValues` (CSPRNG) | Web Crypto |
| Key length | 256 bits | — |
| Nonce length | 96 bits | RFC 8439 |
| AEAD tag | 128 bits | RFC 8439 |
| Key derivation | WebAuthn PRF extension → domain-separated HKDF | W3C WebAuthn L3 |
| Asymmetric (envelope rewrap) | X25519 | RFC 7748 |
| Detached signatures (when used) | Ed25519 | RFC 8032 |

All symmetric / hash / KDF primitives flow through `@sqlitewasmblazor/crypto-core`
(wrapping `@awasm/noble`). Asymmetric primitives use the user agent's
SubtleCrypto. The "single crypto provider" invariant means there is exactly
one implementation of each primitive in the bundle; a BouncyCastle adapter
provides identical algorithms for offline test scenarios.

## 7. Formal verification

The Plane-2 VFS is modeled symbolically with Tamarin. See
[`docs/formal/README.md`](../formal/README.md) and the three `.spthy`
theories under `docs/formal/vfs-tamarin/` for per-slot AEAD soundness,
in-place conversion lifecycle, and key-cache / manifest unlock lemmas.

## 8. Known limitations

| Limitation | Why |
|---|---|
| No defense against whole-disk rollback to a prior valid encrypted snapshot. | The AEAD authenticates pages but not disk monotonicity. A signed manifest at a higher layer can close this if needed. |
| No defense against live-process memory dump after unlock. | The worker holds the global key in clear while a DB is open; this is fundamental to the at-rest model. |
| No defense against same-origin script compromise. | A malicious script running in the page can read decrypted data straight from the worker. CSP, dependency hygiene, and code review are the host's job. |
| Plain export (`.zip`) carries no application-layer authenticity. | Transport is the host's responsibility. The encrypted `.eds` export does carry an envelope MAC and is the recommended cross-device path. |
