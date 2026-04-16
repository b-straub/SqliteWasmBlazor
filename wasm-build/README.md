# SQLCipher WASM Build

Build scripts for a SQLCipher-enabled `sqlite3.wasm` drop-in for `@sqlite.org/sqlite-wasm`.

## Files

| File | Purpose |
|------|---------|
| `prepare-deps.sh` | Clone / verify all external source repos at their pinned versions (step 0) |
| `build-openssl.sh` | Cross-compile OpenSSL 3.x to WASM (`libcrypto.a`) |
| `build-sqlcipher.sh` | Generate SQLCipher amalgamation (`sqlite3.c` + `sqlite3.h`) |
| `patch-gnumakefile.patch` | Adds `SQLCIPHER_*` variable hooks to SQLite's `ext/wasm/GNUmakefile` |
| `build-wasm.sh` | Orchestrates the full WASM build |

## Frozen Dependency Versions

| Dependency | Source | Pinned version | Commit |
|---|---|---|---|
| SQLite | `github.com/sqlite/sqlite` | `version-3.51.3` | `a5333afb9a` |
| SQLCipher | `github.com/sqlcipher/sqlcipher` | `v4.14.0` | `778ab890` |
| OpenSSL | downloaded by `build-openssl.sh` | `3.3.2` | — |
| Emscripten | install via `emsdk` | `5.x` (min `5.0.4`) | — |
| `@sqlite.org/sqlite-wasm` npm | npm | `3.51.2-build8` | — |

## Prerequisites

- **Emscripten SDK 5.x** (minimum `5.0.4`) — `~/emsdk` or set `EMSDK_PATH`
  - Install: `cd ~/emsdk && ./emsdk install 5.0.4 && ./emsdk activate 5.0.4 && source emsdk_env.sh`
- **SQLite source** — `../../sqlite` or set `SQLITE_SRC` (use `prepare-deps.sh` to clone at the pinned tag)
- **SQLCipher source** — `../../sqlcipher` or set `SQLCIPHER_SRC` (use `prepare-deps.sh`)
- Build tools: `make`, `wget`/`curl`, `tar`, `gcc`, `tclsh`, `perl`

## Build Steps

```bash
cd wasm-build/
./build-wasm.sh            # debug build — runs all steps automatically
./build-wasm.sh --release  # release build (emcc -Oz, NDEBUG, extra wasm-opt passes)
```

`build-wasm.sh` automatically calls `prepare-deps.sh`, `build-openssl.sh`, and
`build-sqlcipher.sh` in order, skipping any step whose output already exists (idempotent).

Output: `wasm-build/out/sqlite3.wasm`.
`npm run build` automatically uses this file when present instead of the npm-packaged WASM.

