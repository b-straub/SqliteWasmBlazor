# SqliteWasmBlazor Threat Model

Scope: the public SqliteWasmBlazor surface — the plain SQLite WASM engine
(`src/Base/SqliteWasmBlazor`, Plane 1), the PRF-keyed encryption VFS
(`src/Crypto/SqliteWasmBlazor.Crypto`, Plane 2), and the end-to-end
encrypted multi-device sync layer (`src/CryptoSync/SqliteWasmBlazor.CryptoSync`
+ the `DeltaRelay/` PHP relay, Plane 3). Each plane is independently
shippable: Plane 2 adds at-rest confidentiality on top of Plane 1, and
Plane 3 adds cross-device data flow on top of Plane 2.

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
| Other paired devices (Plane 3) | Trusted up to revocation | Hold their own subkeys derived from their own passkey/PRF seed. Compromise of one is bounded by group-key rotation on revocation. |
| Delta relay (Plane 3, `DeltaRelay/`) | Honest-but-curious | Stores opaque ciphertext blobs in `relay.db`, signs nothing, holds no plaintext keys. May log requests, may serve old ciphertexts, may collude with a network attacker. |
| Network attacker (Plane 3) | Active Dolev-Yao | May intercept, modify, drop, replay, reorder relay traffic. Bounded by HTTPS at the transport layer; treated as full Dolev-Yao at the application layer. |
| Same-origin script (XSS, dependency compromise) | Hostile | Out of scope; addressed by Content Security Policy and code review at higher layers. |
| Endpoint malware, live-process memory dump | Out of scope | Addressed by OS hygiene and hardware. |

## 3. In-scope threats

### Plane 1 + 2 (at-rest)

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
   monotonic history. Plane 3 closes this gap for synced groups via the
   relay's monotonic cursor + per-group key version.
6. **Wrong-key unlock attempt.** A different passkey is presented for an
   existing encrypted disk. Plane 2 rejects via a slot-0 AEAD probe
   (`verifyEncryptionKey`) and a manifest MAC bound to the credential.
7. **Mistargeted import.** A `.eds` envelope encrypted for a different
   recipient is imported. The import path preflights the envelope's MAC and
   page-shape before any destructive operation on the current disk.

### Plane 3 (multi-device sync)

8. **Honest-but-curious relay reads ciphertext + metadata + access
   patterns.** Plane 3 defends payload confidentiality with per-row AES-GCM
   (CEK is per-group, distributed via admin-signed ShareTarget) and binds
   the AAD to `groupContext:keyVersion`. The relay never sees CEKs;
   metadata leakage is bounded to whitelist pubkeys + relay traffic timing.
9. **Active network attacker modifies, drops, replays, or reorders sync
   messages.** Each `DeltaEnvelope` carries an outer Ed25519 signature; each
   per-group batch carries its own Ed25519 signature; the receiver-side
   `IReceiveCursorStore` advances monotonically; replay outside a ±300 s
   timestamp window is rejected.
10. **Compromise of a single paired device.** Future content of other
    devices remains confidential through group-key rotation on revocation
    (one bounded rotation per group is formally modeled). Past content
    accessed by the compromised device cannot be unlearned — that's a
    fundamental limit, not a Plane-3 bug.
11. **Stale views from concurrent clients.** Plane 3 uses a monotonic
    receive cursor per device; envelopes are applied idempotently. No
    last-writer-wins at the relay; conflict resolution happens locally
    against the receiving DbContext.
12. **Relay-side fan-out flooding (DoS).** Plane 3's whitelist-broadcast
    model authenticates POST against a pubkey allow-list; unauthenticated
    fan-out is impossible. Read GETs are signed with a recipient-controlled
    Ed25519 priv key; ±300 s timestamp window bounds replay-and-amplify.
13. **Malicious admin pin/purge.** Pinned-seed reseed (purge) is authority-
    bound to the deployment admin's Ed25519 signing key; purge epoch is
    monotonic and modeled in `05-pin-purge-authority.spthy`.

## 4. Out of scope

- Live-process memory dumps. The crypto primitives wipe transient buffers
  with `CryptographicOperations.ZeroMemory` and worker-side `clearBytes`
  helpers, but a live attacker with full process access can still observe
  derived keys after unlock.
- Same-origin script compromise (XSS, dependency takeover). This is a
  general-purpose offline-first application substrate; the host application
  is responsible for CSP, code review, and dependency hygiene.
- Endpoint malware and hardware-level side channels.
- Bulk-export / import outside Plane 3. If a host application moves data
  between devices via the `.eds` / `.zip` primitives directly (bypassing
  the sync relay), it owns the transport-layer authenticity.

## 5. Channels and defenses

| Channel | Wire format | Authenticity / confidentiality |
|---|---|---|
| .NET ↔ Worker (intra-page) | `postMessage` request/response envelope | Same-origin, structured cloning. No application-layer crypto. |
| Worker ↔ OPFS (Plane 1) | Raw SQLite pages | None — relies on OS file permissions. |
| Worker ↔ OPFS (Plane 2) | `[ciphertext(4096) \| nonce(12) \| tag(16)]` per slot | ChaCha20-Poly1305 AEAD with AAD `"prf-vfs-v1\|" + dbPath + "\|" + slotIndex_LE32`. Slot-0 probe gates unlock. Manifest MAC binds to credential. |
| Whole-disk export (`.eds`) | MessagePacked `EncryptedDiskEnvelope` | ECIES-wrapped slot key under recipient X25519 pubkey; per-slot AEAD; envelope-level MAC over `(credentialIdHint, slot headers)`. |
| Whole-disk export (`.zip` plain) | ZIP of `.db` files | None at this layer. Confidentiality is on Plane 1 storage. |
| Device ↔ Relay POST (Plane 3) | HTTPS POST `/api/delta` | Ed25519 signature over `(timestamp, senderPubkey)` against the relay's whitelist; ±300 s timestamp window. |
| Device ↔ Relay GET (Plane 3) | HTTPS GET `/api/delta?recipient=PK&since=N` | Ed25519 signature over `(timestamp, recipient)` verified against the recipient pubkey from the query; stateless on the relay. |
| Device ↔ Device via relay (Plane 3) | MessagePacked `DeltaEnvelope` | Outer Ed25519 signature over `pack(groups)` + per-group Ed25519 batch signature + per-row AES-GCM AAD `"groupContext:keyVersion"`. Receive cursor monotonicity via `IReceiveCursorStore`. |

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

Both encryption planes are modeled symbolically with Tamarin. See
[`../formal/README.md`](../formal/README.md) for the full list and the
verification commands. Coverage:

- **Plane 2:** `vfs-tamarin/{vfs,vfs-inplace-lifecycle,vfs-cache-import-lifecycle}.spthy`
  — per-slot AEAD soundness, in-place conversion lifecycle, key-cache and
  manifest unlock lemmas.
- **Plane 3:** `cryptosync-tamarin/{01..05}.spthy` — invitation control
  plane (admin-signed bundle, transport-key whitelist lifecycle), group
  key distribution (admin-signed ShareTarget credentials, one bounded
  revocation rotation), delta data plane (envelope + per-group batch
  signatures, per-row AEAD AAD binding), relay whitelist + cursor
  (authorization, revoked-read grace, monotonic acceptance), pin-purge
  authority (admin-only pinned reseed, monotonic purge epoch).

## 8. Known limitations

| Limitation | Why |
|---|---|
| No standalone defense against whole-disk OPFS rollback to a prior valid encrypted snapshot. | The page AEAD authenticates pages but not disk monotonicity. Plane 3's monotonic receive cursor closes this for synced groups; a fully offline host without Plane 3 needs a higher-layer signed manifest if rollback matters. |
| No defense against live-process memory dump after unlock. | The worker holds the global key in clear while a DB is open; this is fundamental to the at-rest model. |
| No defense against same-origin script compromise. | A malicious script running in the page can read decrypted data straight from the worker. CSP, dependency hygiene, and code review are the host's job. |
| Plain export (`.zip`) carries no application-layer authenticity. | Transport is the host's responsibility. The encrypted `.eds` export carries an envelope MAC; Plane 3 delta sync carries per-envelope + per-batch + per-row authenticity. |
| Plane 3 relay rate-limiting is deployment-layer. | The whitelist-broadcast design bounds DoS to whitelisted pubkeys; per-pubkey rate caps are an operator concern, configured at the webserver / reverse-proxy in front of `DeltaRelay/`. |
| Past content cannot be unlearned after device compromise. | Group-key rotation protects future content; history accessed before revocation is fundamentally outside the model. |
