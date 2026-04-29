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
| `cryptosync-relay-gc.php` | **denied** | Time-based delta retention CLI. |
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
| `POST /api/whitelist` | admin Ed25519 sig over canonical sorted member string | `{version, members[], admin_pubkey, admin_signature}` |
| `POST /api/delta` | sender Ed25519 sig in `X-Sender-Sig` header (whitelisted as `active`) | `{envelope: base64}` |
| `GET /api/delta?since=N&pubkey=PK` | puller Ed25519 sig in `X-Sig` (whitelisted as `active` or within `read_grace_seconds` of `revoked`) | _(query only)_ |

Rejection codes: `400` malformed, `401` auth/sig/timestamp failure, `403` not whitelisted / past grace window, `409` whitelist version replay, `413` body over `max_body_bytes`, `500` server error (details in `error_log`, never echoed).

## Integration test workflow

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
dotnet test SqliteWasmBlazor.CryptoSync.Tests/SqliteWasmBlazor.CryptoSync.Tests.csproj \
    -p:SkipVitest=true --filter "Category=LiveRelay"
```

The category trait keeps these tests out of the default `dotnet test` run, so CI without Herd doesn't break.

**Per-test setup pattern** (handled by `HttpSyncTransportLiveRelayTests.InitializeAsync`):

```text
1. write relay-config.php with a fresh synthetic deployment_salt + admin_pubkey_hash
2. delete relay.db (and -wal / -shm if present)
3. POST /api/whitelist with the synthetic admin key, seeding one active sender entry
4. exercise the C# transport against http://delta-relay.test/api/...
5. assert wire contract end-to-end
```

Schema creation is idempotent (`CREATE TABLE IF NOT EXISTS`), so deleting `relay.db` between tests is safe. The seeded `relay-config.php` is left in place after the run for post-mortem; the next run overwrites it.

## Retention / GC

`cryptosync-relay-gc.php` is a cron-driven CLI that deletes rows from `deltas` where `created_at < (now - retention_seconds)`. `retention_seconds` is read from `relay-config.php` (default 2592000 = 30 days). Suggested cron:

```cron
0 3 * * * cd /path/to/DeltaRelay && php cryptosync-relay-gc.php >> gc.log 2>&1
```

The CLI emits one JSON line on stdout per run:

```json
{"deleted": 17, "oldest_remaining": 1714000000}
```

`oldest_remaining` is the `MIN(created_at)` of the surviving rows (or `null` if the queue is now empty). Exit code is `0` on success, `1` on failure (error to stderr, nothing on stdout).

GC is **lossy**: a receiver offline longer than `retention_seconds` silently misses intervening envelopes. Lossless GC requires snapshot endpoints, which are deferred (see ROADMAP).

Whitelist entries (`whitelist` rows + `whitelist_meta.current_version`) are **never** GC'd — they transition `active → revoked → expired` and stay forever to keep the whitelist version monotonic across re-additions.

The script refuses to run over HTTP (`php_sapi_name() !== 'cli'` → 404). The Valet driver and `.htaccess` also deny direct access; verify with:

```sh
curl -I https://your-host/cryptosync-relay-gc.php   # expect 403 or 404
```

## Server hardening (deployment-time, not in code)

These are operator concerns the relay code does not enforce:

- `relay-config.php` and `relay.db` mode `0600`, owned by the PHP user.
- nginx/Apache: `client_max_body_size 1m`, per-IP rate-limit (`limit_req_zone`), `Server:` header stripped, no `server_tokens`.
- `php.ini`: `expose_php=Off`, `display_errors=Off`, `log_errors=On` to a file outside web root.
- TLS 1.3 only, HSTS, no HTTP fallback.
- Daily `relay.db` backup to encrypted off-host storage (idempotent receiver semantics make restoration safe).
- Monitor: alert when whitelist-push 401s spike (signal: forgery attempt against `admin_pubkey_hash`), POST 403s spike (signal: revoked-user retry storm).
