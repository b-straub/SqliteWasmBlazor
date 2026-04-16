#!/usr/bin/env bash
# Build sqlite3.wasm with SQLCipher encryption support.
#
# This is the one-stop build script: it automatically runs prepare-deps.sh,
# build-openssl.sh, and build-sqlcipher.sh as needed (all idempotent — safe
# to call multiple times; each step is skipped when its output already exists).
#
# Output: wasm-build/out/sqlite3.wasm (drop-in for @sqlite.org/sqlite-wasm)
#
# Usage:
#   ./build-wasm.sh            # debug build (runs all steps automatically)
#   ./build-wasm.sh --release  # emcc -Oz + wasm-opt -Oz

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="${SCRIPT_DIR}/out"
_SQLITE_DEFAULT="$(cd "${SCRIPT_DIR}/../../sqlite" 2>/dev/null && pwd || echo "${SCRIPT_DIR}/../../sqlite")"
SQLITE_SRC="${SQLITE_SRC:-${_SQLITE_DEFAULT}}"
# Build in sqlite/ext/wasm-sqlcipher so dir.top=../.. resolves to the SQLite root.
BUILD_DIR="${SQLITE_SRC}/ext/wasm-sqlcipher"
JOBS="${JOBS:-$(nproc 2>/dev/null || echo 4)}"
OPT=""  # set to -Oz by --release

# Use a modern Emscripten (5.x) to match the feature set used by @sqlite.org/sqlite-wasm.
# The JS glue patch (patches/@sqlite.org+sqlite-wasm+*.patch) adds the 3 extra imports that
# 5.x WASM requires (_abort_js, __syscall_renameat, __syscall_getdents64) so any 5.x version
# works. The minimum verified version is 5.0.4 (the Docker latest at npm package publish time).
# NOTE: 3.1.56 in the CI workflows is only for the native stub, not this WASM build.
EMCC_REQUIRED_VERSION="5.0.4"
EMCC_REQUIRED_MAJOR="5"
NPM_WASM_VERSION="3.51.2-build8"
#
# COMPATIBILITY NOTE: the @sqlite.org/sqlite-wasm npm package (3.51.2-build8) was built from
# SQLite's "wasm-post-3.51" dev branch (SQLite ~3.52), which extended sqlite3_kvvfs_methods
# with WASM-specific fields (pVfs, pIoDb, pIoJrnl, nBufferSize).  The SQLite mainline release
# used here (3.51.3) does NOT have those extra fields.  The JS glue patch handles this via an
# early-return guard in the kvvfs bootstrap initializer, so the C-only kvvfs implementation
# stays active while OPFS SAHPool VFS (the VFS actually used by this project) works normally.

# --- parse args ---
while [[ $# -gt 0 ]]; do
    case "$1" in
        --release) OPT="-Oz"; shift ;;
        *) echo "Unknown arg: $1" >&2; exit 1 ;;
    esac
done

# --- activate Emscripten ---
if ! command -v emcc &>/dev/null; then
    EMSDK_PATH="${EMSDK_PATH:-${HOME}/emsdk}"
    if [[ -f "${EMSDK_PATH}/emsdk_env.sh" ]]; then
        # shellcheck disable=SC1091
        source "${EMSDK_PATH}/emsdk_env.sh" >/dev/null 2>&1
    else
        echo "ERROR: emcc not found. Install Emscripten SDK or set EMSDK_PATH." >&2
        exit 1
    fi
fi

# --- verify Emscripten version ---
EMCC_ACTUAL_VERSION="$(emcc --version 2>/dev/null | head -1 | grep -oP '\d+\.\d+\.\d+' | head -1)"
EMCC_ACTUAL_MAJOR="$(echo "${EMCC_ACTUAL_VERSION}" | cut -d. -f1)"
if [[ "${EMCC_ACTUAL_MAJOR}" != "${EMCC_REQUIRED_MAJOR}" ]]; then
    echo "ERROR: emcc ${EMCC_ACTUAL_VERSION} found, but Emscripten ${EMCC_REQUIRED_MAJOR}.x is required." >&2
    echo "       The WASM binary must be compiled with emcc ${EMCC_REQUIRED_MAJOR}.x; the JS glue patch" >&2
    echo "       (patches/@sqlite.org+sqlite-wasm+*.patch) covers the import differences within that" >&2
    echo "       major version. Minimum verified: ${EMCC_REQUIRED_VERSION}." >&2
    echo "" >&2
    echo "Fix:" >&2
    echo "  cd \${EMSDK_PATH:-\$HOME/emsdk}" >&2
    echo "  ./emsdk install ${EMCC_REQUIRED_VERSION}" >&2
    echo "  ./emsdk activate ${EMCC_REQUIRED_VERSION}" >&2
    echo "  source emsdk_env.sh" >&2
    echo "  # then re-run this script" >&2
    exit 1
fi

# --- step 0: ensure source repos are at the pinned versions ---
echo "=== Step 0: Preparing source dependencies ==="
"${SCRIPT_DIR}/prepare-deps.sh"
echo ""

# --- step 1: OpenSSL WASM static lib (idempotent: skipped if already built) ---
OPENSSL_LIB="${OUT_DIR}/openssl/lib/libcrypto.a"
if [[ ! -f "${OPENSSL_LIB}" ]]; then
    echo "=== Step 1: Building OpenSSL for WASM ==="
    "${SCRIPT_DIR}/build-openssl.sh"
    echo ""
else
    echo "=== Step 1: OpenSSL already built — skipping ==="
    echo ""
fi

# --- step 2: SQLCipher amalgamation (idempotent: skipped if already built) ---
SQLCIPHER_C="${OUT_DIR}/sqlcipher.c"
SQLCIPHER_H="${OUT_DIR}/sqlcipher.h"
if [[ ! -f "${SQLCIPHER_C}" || ! -f "${SQLCIPHER_H}" ]]; then
    echo "=== Step 2: Generating SQLCipher amalgamation ==="
    "${SCRIPT_DIR}/build-sqlcipher.sh"
    echo ""
else
    echo "=== Step 2: SQLCipher amalgamation already generated — skipping ==="
    echo ""
fi

OPENSSL_INC="${OUT_DIR}/openssl/include"

if [[ ! -d "${SQLITE_SRC}/ext/wasm" ]]; then
    echo "ERROR: SQLite source directory not found: ${SQLITE_SRC}" >&2
    echo "  Run ./prepare-deps.sh first, or set SQLITE_SRC." >&2
    exit 1
fi

echo "=== Step 3: Building SQLCipher WASM ==="
echo "  emcc:           $(emcc --version 2>/dev/null | head -1)"
echo "  SQLCipher:      ${SQLCIPHER_C}"
echo "  OpenSSL lib:    ${OPENSSL_LIB}"
echo "  SQLite src:     ${SQLITE_SRC}"
echo ""

# --- bootstrap: ensure sqlite3.c + Makefile exist in the SQLite source root ---
# Utility tools in GNUmakefile need $(dir.top)/sqlite3.c compiled with the HOST compiler.
if [[ ! -f "${SQLITE_SRC}/sqlite3.c" || ! -f "${SQLITE_SRC}/Makefile" ]]; then
    echo "--- Bootstrapping SQLite source tree (configure + sqlite3.c) ---"
    pushd "${SQLITE_SRC}" > /dev/null
    ./configure --disable-tcl 2>&1
    make sqlite3.c sqlite3.h 2>&1
    popd > /dev/null
    echo "--- Bootstrap complete ---"
    echo ""
fi

# --- set up a clean working copy of ext/wasm inside the SQLite source tree ---
# Must be at ext/wasm-sqlcipher/ so dir.top=../.. still resolves to the SQLite root.
rm -rf "${BUILD_DIR}"
cp -r "${SQLITE_SRC}/ext/wasm" "${BUILD_DIR}"
# Ensure all files are owner-writable before patching.
chmod -R u+w "${BUILD_DIR}"

# Apply the patch to add SQLCIPHER_EXTRA_CFLAGS / SQLCIPHER_LINK_FLAGS / SQLCIPHER_INCLUDE_FLAGS
cd "${BUILD_DIR}"
patch -p3 < "${SCRIPT_DIR}/patch-gnumakefile.patch"

# Ensure sqlite3_next_stmt is exported. It was added to the wasm-post-3.51 dev branch
# in Nov 2025 but has not yet merged to the SQLite trunk. The npm package (@sqlite.org/sqlite-wasm)
# was built from a source tree that includes it, so our WASM must export it too.
EXPORTS_CORE="${BUILD_DIR}/api/EXPORTED_FUNCTIONS.sqlite3-core"
if [[ -f "${EXPORTS_CORE}" ]] && ! grep -q "_sqlite3_next_stmt" "${EXPORTS_CORE}"; then
    echo "_sqlite3_next_stmt" >> "${EXPORTS_CORE}"
fi

# Patch sqlcipher.c: remove the .fini_array section attribute added in SQLCipher 4.9.0.
# `.fini_array` is unsupported in WASM and can crash the LLVM wasm backend during cleanup.
# See: https://github.com/7mind/sqlcipher-wasm/blob/main/lib.sh (patch_amalgamation)
# We patch a local copy so the source artifact at ${SQLCIPHER_C} is not modified.
SQLCIPHER_PATCHED_C="${OUT_DIR}/sqlcipher-patched.c"
cp "${SQLCIPHER_C}" "${SQLCIPHER_PATCHED_C}"
sed -i 's/__attribute__((used, section(".fini_array")))//' "${SQLCIPHER_PATCHED_C}"
# Restore original mtime so GNUmakefile's freshness check against sqlite3.h still passes.
touch --reference="${SQLCIPHER_C}" "${SQLCIPHER_PATCHED_C}"

# --- create config.make (normally generated by the configure script) ---
EMCC_BIN="$(command -v emcc)"
BASH_BIN="$(command -v bash)"
# Prefer wabt wasm-strip; fall back to llvm-strip from emsdk.
WASM_STRIP_BIN="$(command -v wasm-strip 2>/dev/null || echo '')"
if [[ -z "${WASM_STRIP_BIN}" ]]; then
    _EMSDK_PATH="${EMSDK_PATH:-${HOME}/emsdk}"
    [[ -x "${_EMSDK_PATH}/upstream/bin/llvm-strip" ]] && WASM_STRIP_BIN="${_EMSDK_PATH}/upstream/bin/llvm-strip"
fi
# wasm-opt from Binaryen (bundled with emsdk).
WASM_OPT_BIN="$(command -v wasm-opt 2>/dev/null || echo '')"
if [[ -z "${WASM_OPT_BIN}" ]]; then
    _EMSDK_PATH="${EMSDK_PATH:-${HOME}/emsdk}"
    [[ -x "${_EMSDK_PATH}/upstream/bin/wasm-opt" ]] && WASM_OPT_BIN="${_EMSDK_PATH}/upstream/bin/wasm-opt"
fi

cat > "${BUILD_DIR}/config.make" <<EOF
bin.bash     = ${BASH_BIN}
bin.emcc     = ${EMCC_BIN}
bin.wasm-strip = ${WASM_STRIP_BIN:-}
bin.wasm-opt = ${WASM_OPT_BIN:-}
SHELL        = ${BASH_BIN}
SHELL_OPT    = -DSQLITE_THREADSAFE=0
SHELL_DEP    = \$(dir.top)/sqlite3.c
EOF

# --- build ---
# SQLCipher / crypto CFLAGS
SQLCIPHER_CFLAGS="-DSQLITE_HAS_CODEC -DSQLCIPHER_CRYPTO_OPENSSL"
SQLCIPHER_CFLAGS+=" -I${OPENSSL_INC}"
SQLCIPHER_CFLAGS+=" -USQLITE_THREADSAFE -DSQLITE_THREADSAFE=1"
SQLCIPHER_CFLAGS+=" -DSQLITE_EXTRA_INIT=sqlcipher_extra_init -DSQLITE_EXTRA_SHUTDOWN=sqlcipher_extra_shutdown"
# Release: strip debug assertions.
[[ -n "${OPT}" ]] && SQLCIPHER_CFLAGS+=" -DNDEBUG -DSQLITE_NDEBUG"

echo "Running GNUmakefile..."
make -C "${BUILD_DIR}" \
    "sqlite3.c=${SQLCIPHER_PATCHED_C}" \
    "SQLCIPHER_EXTRA_CFLAGS=${SQLCIPHER_CFLAGS}" \
    "SQLCIPHER_LINK_FLAGS=${OPENSSL_LIB}" \
    ${OPT:+"emcc_opt=${OPT}"} \
    -j"${JOBS}" \
    jswasm/sqlite3.wasm \
    2>&1

WASM_OUT="${BUILD_DIR}/jswasm/sqlite3.wasm"
if [[ ! -f "${WASM_OUT}" ]]; then
    echo "ERROR: jswasm/sqlite3.wasm not produced." >&2
    exit 1
fi

# --- copy output ---
cp "${WASM_OUT}" "${OUT_DIR}/sqlite3.wasm"

# Fallback strip when wasm-strip was not found during config.make generation
if [[ -z "${WASM_STRIP_BIN}" && -n "$(command -v llvm-strip 2>/dev/null)" ]]; then
    llvm-strip --strip-debug "${OUT_DIR}/sqlite3.wasm"
elif [[ -z "${WASM_STRIP_BIN}" ]]; then
    echo "  (wasm-strip/llvm-strip not found — debug sections retained; install wabt or emsdk)"
fi

# --- additional wasm-opt pass ---
if [[ -n "${WASM_OPT_BIN}" ]]; then
    BEFORE=$(wc -c < "${OUT_DIR}/sqlite3.wasm")
    OPT_PASSES="-Oz --enable-bulk-memory --all-features --post-emscripten --strip-debug --local-cse"
    OPT_PASSES+=' --vacuum --reorder-functions --reorder-locals'
    # Release: extra dead-code, dedup, and instruction-level peephole passes
    [[ -n "${OPT}" ]] && OPT_PASSES+=' --code-folding --duplicate-function-elimination --strip-producers --optimize-instructions'
    # shellcheck disable=SC2086
    "${WASM_OPT_BIN}" $OPT_PASSES \
        "${OUT_DIR}/sqlite3.wasm" -o "${OUT_DIR}/sqlite3.wasm"
    AFTER=$(wc -c < "${OUT_DIR}/sqlite3.wasm")
    echo "  wasm-opt: $(( BEFORE / 1024 ))K → $(( AFTER / 1024 ))K"
fi
BYTES_CIPHER=$(wc -c < "${OUT_DIR}/sqlite3.wasm")
echo ""
echo "✓ SQLCipher WASM build complete!"
echo "  Output: ${OUT_DIR}/sqlite3.wasm  ($(( BYTES_CIPHER / 1024 )) KB)"

# --- size comparison vs standard @sqlite.org/sqlite-wasm ---
STOCK_WASM="${SCRIPT_DIR}/../SqliteWasmBlazor/TypeScript/node_modules/@sqlite.org/sqlite-wasm/dist/sqlite3.wasm"
echo ""
echo "Size comparison:"
if [[ -f "${STOCK_WASM}" ]]; then
    BYTES_STOCK=$(wc -c < "${STOCK_WASM}")
    echo "  Standard sqlite3.wasm : $(( BYTES_STOCK  / 1024 )) KB"
    echo "  SQLCipher sqlite3.wasm: $(( BYTES_CIPHER / 1024 )) KB  (+$(( (BYTES_CIPHER - BYTES_STOCK) / 1024 )) KB)"
else
    echo "  SQLCipher sqlite3.wasm: $(( BYTES_CIPHER / 1024 )) KB"
    echo "  (standard wasm not found at ${STOCK_WASM} — run 'npm install' to enable comparison)"
fi

echo ""
echo "  Run 'npm run build' from SqliteWasmBlazor/TypeScript/ to pick it up."
