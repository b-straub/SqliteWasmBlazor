# Delta Relay (PHP)

Whitelist-authenticated broadcast envelope buffer for CryptoSync's encrypted delta sync. The relay is **honest-but-curious**: it stores opaque envelopes plus a hashed-pubkey whitelist, and never inspects payloads. All confidentiality and integrity guarantees come from the V2 envelope crypto in the C# / TypeScript clients.

For protocol detail see `docs/security/relay-whitelist-design.md`. For the active rewrite plan see `~/.claude/plans/whitelist-broadcast-rewrite.md`.

## Files in this directory

| File | Web-accessible? | Purpose |
|---|---|---|
| `delta-relay.php` | yes (via `/api/*`) | The three endpoints. |
| `index.php` | yes (`/`) | Minimal landing page. No version/path disclosure. |
| `relay-config.example.php` | **denied** | Template for operator config. |
| `relay-config.php` | **denied** | Live deployment config. Created by `cryptosync-relay-init`. |
| `cryptosync-relay-init.php` | **denied** | One-time bootstrap CLI. |
| `relay.db` | **denied** | SQLite store. Auto-created on first request. |
| `LocalValetDriver.php` | n/a | Laravel Valet routing for local dev. |
| `.htaccess` | n/a | Apache routing + deny rules. Must mirror the Valet driver. |

The deny list lives in two places (`LocalValetDriver.php::DENY_PREFIXES` and `.htaccess`'s `RewriteRule ... [F,L]` block). Changes to one must be reflected in the other.

## First-time deployment

1. **Generate the admin Ed25519 keypair on the admin device.** (For Stage A integration tests: a synthetic keypair from `sodium_crypto_sign_keypair` is fine.)
2. **Copy `relay-config.example.php` → `relay-config.php`** and run the init CLI:
   ```sh
   cp relay-config.example.php relay-config.php
   php cryptosync-relay-init.php <admin-pubkey-base64>
   chmod 0600 relay-config.php
   ```
   The CLI generates a random 32-byte `deployment_salt`, computes `admin_pubkey_hash = sha256(salt || pubkey)`, and writes them into `relay-config.php`. It refuses to overwrite an existing config without `--force`.
3. **Verify the config is not web-accessible:**
   ```sh
   curl -I https://your-host/relay-config.php   # expect 403 or 404
   curl -I https://your-host/cryptosync-relay-init.php  # expect 403 or 404
   ```
4. **Admin device pushes the first whitelist** via `POST /api/whitelist` with `version=1, members=[{admin_pubkey_hash, "active"}]`. From this point the deployment is live and the admin can POST/GET deltas like any other client.

## Admin recovery / re-init

Losing the admin device requires operator-level recovery:

```sh
php cryptosync-relay-init.php <new-admin-pubkey-base64> --force
```

This is destructive — the new admin pubkey replaces the old, and clients re-pair from scratch. There is currently no in-protocol admin recovery (signed key bundle is captured as a future enhancement in `relay-whitelist-design.md` §10).

## Wire contract

| Endpoint | Auth | Body / Query |
|---|---|---|
| `POST /api/whitelist` | admin Ed25519 sig over canonical sorted member string | `{version, operations[], admin_pubkey, admin_signature}` |
| `POST /api/delta` | sender Ed25519 sig in `X-Sender-Sig` header (whitelisted as `active`); optional `X-Admin-Pin-Sig` for admin-pinned reseed | `{envelope: base64}` |
| `GET /api/delta?since=N&pubkey=PK` | puller Ed25519 sig in `X-Sig` (whitelisted as `active` or within `read_grace_seconds` of `revoked`) | _(query only)_ |

Rejection codes: `400` malformed, `401` auth/sig/timestamp failure, `403` not whitelisted / past grace window / pin authority denied (sender ≠ deployment admin), `409` whitelist version replay, `413` body over `max_body_bytes`, `500` server error (details in `error_log`, never echoed).

### Admin-pinned reseed

A regular `POST /api/delta` may include the optional header `X-Admin-Pin-Sig` carrying a base64 Ed25519 signature over `deltapin-v1|<X-Timestamp>|sha256(envelope) hex`. The relay accepts the pin only when:

- the sender (`X-Sender-PubKey`) hash equals the deployment `admin_pubkey_hash` — i.e. the sender IS the deployment admin, and
- the pin signature verifies against the same key that signed `X-Sender-Sig`.

On success, in one transaction the relay deletes **every** prior row in `deltas` (pinned + unpinned alike) and stores the new envelope with `pinned=1`. This is reseed semantics: the new envelope is the canonical baseline, every delta before it is orphaned by definition (no receiver replays anything before the seed). `AUTOINCREMENT` is preserved across the purge so existing receivers' `since=N` keeps working — the next GET picks up the new pin at a fresh higher cursor. The 200 response body grows two extra fields:

```json
{"cursor": 42, "pinned": true, "prior_rows_purged": 17}
```

There is **no separate unpin or unseed endpoint, and no autonomous time-based GC.** The admin pin POST is the *sole* delete authority on this relay — every purge is initiated by the deployment admin's signature. Letting any whitelisted client trigger a purge would expose the system to **censorship-by-omission**: a malicious whitelisted client could pull all pending patches, post a truncated rollup, and the relay would erase patches that other clients hadn't yet received. Admin-only purge gates that whole class of attack at the wire layer. To drop the seed without replacing it, drop the relay deployment.

The same property means: **if the admin is offline, the relay grows unboundedly until the admin returns and reseeds.** That's an accepted trade-off — the admin already controls the keys and is the trust anchor for everything else. Multi-party / quorum compaction (where M-of-N whitelisted peers can co-sign a rollup) is a future enhancement; see ROADMAP.

### Fragmentation hint (`gc_requested`)

When the unpinned-row count in `deltas` exceeds `gc_threshold_rows` (config), `GET /api/delta` responses include an extra field:

```json
{"cursor": 102, "envelopes": [...], "gc_requested": true}
```

This is **purely informational** — the relay never deletes on its own. The admin client reads the flag and may respond with a fresh pin POST. Non-admin clients observe the flag for diagnostics but cannot act on it (their pin POST would 403). Lower the threshold to nudge the admin sooner; raise it to leave them alone longer.

## Integration test workflow

### Self-contained PHP relay test

For a quick endpoint-level check that does not require Herd/Valet and does not
touch the live `DeltaRelay/relay.db`, run:

```sh
XDEBUG_MODE=off php DeltaRelay/tests/relay-integration.php
```

The harness copies the relay into a temporary directory, writes a synthetic
`relay-config.php`, starts PHP's built-in server on `127.0.0.1`, signs real
Ed25519 whitelist/delta requests with sodium, and tears the temp deployment
down after success. Set `DELTA_RELAY_KEEP_TEST_DIR=1` to keep the temp relay
directory for inspection.

Covered behaviors:

- `cryptosync-relay-init.php` config generation, admin hash, overwrite refusal,
  `--force`, and `0600` mode;
- `.htaccess` / Valet deny-rule drift checks for private relay files;
- direct access to private relay files is denied;
- admin whitelist pushes, forged admin signatures, and version replay rejection;
- active sender POST, signed broadcast GET, and `since` cursor filtering;
- forged sender and receiver signatures;
- non-whitelisted and revoked POST rejection;
- revoked read grace and expired-grace rejection;
- `max_body_bytes` rejection;
- admin-only pinned reseed purge and non-admin pin rejection.

### Live C# transport suite

The Stage A integration suite (xUnit, grows incrementally over Steps 2-6) drives a live Herd-served PHP relay with a known synthetic admin keypair.

**One-time Herd link** (single command, reversible via `herd unlink delta-relay`):

```sh
cd DeltaRelay
herd link delta-relay
# Confirm: curl -s -o /dev/null -w "%{http_code}\n" http://delta-relay.test/  → 200
```

(Older `valet`-based setups: `valet link delta-relay` from the same directory.)

**Run the suite**:

```sh
RUN_LIVE_RELAY_TESTS=1 \
dotnet test SqliteWasmBlazor.CryptoSync.Tests/SqliteWasmBlazor.CryptoSync.Tests.csproj \
    -p:SkipVitest=true --filter "Category=LiveRelay"
```

Without `RUN_LIVE_RELAY_TESTS=1`, these facts are reported as skipped. The
category filter selects only the live relay suite when you do opt in.

**Per-test setup pattern** (handled by `HttpSyncTransportLiveRelayTests.InitializeAsync`):

```text
1. write relay-config.php with a fresh synthetic deployment_salt + admin_pubkey_hash
2. delete relay.db (and -wal / -shm if present)
3. POST /api/whitelist with the synthetic admin key, seeding one active sender entry
4. exercise the C# transport against http://delta-relay.test/api/...
5. assert wire contract end-to-end
```

Schema creation is idempotent (`CREATE TABLE IF NOT EXISTS`), so deleting `relay.db` between tests is safe. The seeded `relay-config.php` is left in place after the run for post-mortem; the next run overwrites it.

## Storage growth and admin compaction

The relay never autonomously prunes anything — neither cron nor a built-in scheduler. Storage growth is bounded entirely by the deployment admin's reseed cadence (the admin pin POST documented above). Operational lifecycle:

1. Admin publishes initial seed (pin POST). `deltas` holds one pinned row.
2. Whitelisted senders post patches normally; relay accumulates `[seed, p1, p2, …, pN]`.
3. When `N > gc_threshold_rows`, every `GET /api/delta` includes `"gc_requested": true` until the next reseed.
4. The admin client (the only identity that can pin) sees the flag, derives a compacted seed locally from the current state, and publishes it via a fresh pin POST.
5. The relay atomically purges seed + N patches in one transaction and stores the new seed with `pinned=1`. `gc_requested` clears on subsequent GETs.

If the admin is offline, the relay grows past the threshold and stays there. Senders keep posting (no relay-side throttling beyond `max_body_bytes`); receivers keep pulling and observing `gc_requested: true`. This is the accepted trade-off for keeping purge authority gated to admin signatures alone — the censorship-by-omission attack is closed at the cost of admin-availability sensitivity. See ROADMAP for deferred work on quorum/multi-party compaction that loosens this.

Whitelist entries (`whitelist` rows + `whitelist_meta.current_version`) are **never** purged — they persist forever to keep `current_version` monotonic across re-additions.

## Server hardening (deployment-time, not in code)

These are operator concerns the relay code does not enforce:

- `relay-config.php` and `relay.db` mode `0600`, owned by the PHP user.
- nginx/Apache: `client_max_body_size 1m`, per-IP rate-limit (`limit_req_zone`), `Server:` header stripped, no `server_tokens`.
- `php.ini`: `expose_php=Off`, `display_errors=Off`, `log_errors=On` to a file outside web root.
- TLS 1.3 only, HSTS, no HTTP fallback.
- Daily `relay.db` backup to encrypted off-host storage (idempotent receiver semantics make restoration safe).
- Monitor: alert when whitelist-push 401s spike (signal: forgery attempt against `admin_pubkey_hash`), POST 403s spike (signal: revoked-user retry storm).
