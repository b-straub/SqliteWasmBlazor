#!/usr/bin/env bash
# Generate the SQLCipher amalgamation (sqlite3.c + sqlite3.h) from the
# SQLCipher source tree at ../sqlcipher (or SQLCIPHER_SRC).
# Output: $SCRIPT_DIR/out/sqlcipher.c and $SCRIPT_DIR/out/sqlcipher.h
#
# Uses the HOST compiler (not emcc); the resulting sqlite3.c bakes in
# SQLCipher crypto under #ifdef SQLITE_HAS_CODEC.
#
# Prerequisites: gcc/clang, make, tclsh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SQLCIPHER_SRC="${SQLCIPHER_SRC:-${SCRIPT_DIR}/../../sqlcipher}"
OUT_DIR="${SCRIPT_DIR}/out"
JOBS="${JOBS:-$(nproc 2>/dev/null || echo 4)}"

if [[ ! -d "${SQLCIPHER_SRC}" ]]; then
    echo "ERROR: SQLCipher source directory not found: ${SQLCIPHER_SRC}" >&2
    echo "  Clone it with: git clone https://github.com/sqlcipher/sqlcipher.git ../../sqlcipher" >&2
    exit 1
fi

# Skip if already built
if [[ -f "${OUT_DIR}/sqlcipher.c" && -f "${OUT_DIR}/sqlcipher.h" ]]; then
    echo "SQLCipher amalgamation already at ${OUT_DIR}. Delete to rebuild."
    exit 0
fi

mkdir -p "${OUT_DIR}"

# Build in a clean copy to avoid polluting the source tree
BUILD_DIR="${SCRIPT_DIR}/src/sqlcipher-build"
rm -rf "${BUILD_DIR}"
mkdir -p "${SCRIPT_DIR}/src"
cp -r "${SQLCIPHER_SRC}" "${BUILD_DIR}"

cd "${BUILD_DIR}"

echo "Configuring SQLCipher (host compiler, amalgamation only)..."
# --with-tempstore=yes: SQLITE_TEMP_STORE=2 in the amalgamation
# -DSQLITE_HAS_CODEC: includes SQLCipher codec sources
./configure \
    --with-tempstore=yes \
    CFLAGS="-DSQLITE_HAS_CODEC" \
    2>&1

echo "Generating SQLCipher amalgamation..."
make -j"${JOBS}" sqlite3.c 2>&1

# Verify
if [[ ! -f "sqlite3.c" || ! -f "sqlite3.h" ]]; then
    echo "ERROR: sqlite3.c / sqlite3.h not generated." >&2
    exit 1
fi

cp sqlite3.c "${OUT_DIR}/sqlcipher.c"
cp sqlite3.h "${OUT_DIR}/sqlcipher.h"
# sqlite3.h must sit next to sqlcipher.c (GNUmakefile derives it by path)
cp sqlite3.h "${OUT_DIR}/sqlite3.h"

# Guard .fini_array usage for Emscripten (cleanup handled via SQLITE_EXTRA_SHUTDOWN).
python3 - "${OUT_DIR}/sqlcipher.c" << 'PYEOF'
import sys
path = sys.argv[1]
with open(path) as f:
    src = f.read()
old = ('#else\nstatic void (*const sqlcipher_fini_func)(void)'
       ' __attribute__((used, section(".fini_array")))'
       ' = sqlcipher_fini;\n#endif')
new = ('#elif !defined(__EMSCRIPTEN__)\nstatic void (*const sqlcipher_fini_func)(void)'
       ' __attribute__((used, section(".fini_array")))'
       ' = sqlcipher_fini;\n#endif')
if old not in src:
    print("WARNING: .fini_array guard pattern not found in sqlcipher.c — skipping patch", flush=True)
else:
    with open(path, 'w') as f:
        f.write(src.replace(old, new, 1))
    print("  Applied .fini_array -> __EMSCRIPTEN__ guard", flush=True)
PYEOF

C_LINES=$(wc -l < "${OUT_DIR}/sqlcipher.c")
echo ""
echo "✓ SQLCipher amalgamation complete!"
echo "  ${OUT_DIR}/sqlcipher.c  (${C_LINES} lines)"
echo "  ${OUT_DIR}/sqlcipher.h  (SQLCipher-modified API)"
echo "  ${OUT_DIR}/sqlite3.h    (alias — needed by SQLite GNUmakefile)"
