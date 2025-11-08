#!/bin/bash
# Build minimal SQLite stub (fast - compiles in <1 second)

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_DIR="${SCRIPT_DIR}/lib"
EMSDK_PATH="/Users/berni/Projects/emsdk"

echo "========================================="
echo "Minimal SQLite Stub Build (Fast)"
echo "========================================="
echo ""

# Source Emscripten
if [ -f "${EMSDK_PATH}/emsdk_env.sh" ]; then
    source "${EMSDK_PATH}/emsdk_env.sh" > /dev/null 2>&1
else
    echo "ERROR: Emscripten not found at ${EMSDK_PATH}"
    exit 1
fi

# Create output directory
mkdir -p "${OUTPUT_DIR}"

# Compile stub (fast!)
echo "Compiling sqlite3_stub.c..."
emcc -O3 \
    -c "${SCRIPT_DIR}/sqlite3_stub.c" \
    -o "${OUTPUT_DIR}/sqlite3_stub.o"

# Create static library
echo "Creating library..."
emar rcs "${OUTPUT_DIR}/e_sqlite3.a" "${OUTPUT_DIR}/sqlite3_stub.o"

# Check result
if [ -f "${OUTPUT_DIR}/e_sqlite3.a" ]; then
    SIZE=$(du -h "${OUTPUT_DIR}/e_sqlite3.a" | cut -f1)
    echo ""
    echo "✓ Build successful!"
    echo ""
    echo "Output: ${OUTPUT_DIR}/e_sqlite3.a"
    echo "Size:   ${SIZE}"
    echo ""
    echo "This stub provides native symbols for P/Invoke."
    echo "All database operations go through JS worker bridge."
    echo ""
else
    echo "ERROR: Build failed"
    exit 1
fi
