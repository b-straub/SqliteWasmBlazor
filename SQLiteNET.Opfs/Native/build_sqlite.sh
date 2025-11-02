#!/bin/bash
#
# build_sqlite.sh
# Build SQLite with VFS tracking for Emscripten/WASM
#

set -e  # Exit on error

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SQLITE_VERSION="3500400"  # SQLite 3.50.4 (matches SQLitePCL.raw)
SQLITE_YEAR="2025"
SQLITE_DIR="${SCRIPT_DIR}/sqlite"
BUILD_DIR="${SCRIPT_DIR}/build"
OUTPUT_DIR="${SCRIPT_DIR}/lib"
EMSDK_PATH="/Users/berni/Projects/emsdk"

# Option to use existing SQLite from SQLitePCL.raw
SQLITE_PCL_RAW="/Users/berni/Projects/SQLitePCL.raw/custom_build_jsvfs/sqlite-amalgamation-${SQLITE_VERSION}"

echo "==================================="
echo "SQLite + VFS Tracking Build Script"
echo "==================================="
echo ""

# Source Emscripten environment (version 3.1.56 - matches .NET WASM runtime)
if [ -f "${EMSDK_PATH}/emsdk_env.sh" ]; then
    echo "Loading Emscripten from ${EMSDK_PATH}..."
    source "${EMSDK_PATH}/emsdk_env.sh"
else
    echo "ERROR: Emscripten not found at ${EMSDK_PATH}"
    echo "Please install Emscripten:"
    echo "  git clone https://github.com/emscripten-core/emsdk.git ${EMSDK_PATH}"
    echo "  cd ${EMSDK_PATH}"
    echo "  ./emsdk install 3.1.56"
    echo "  ./emsdk activate 3.1.56"
    exit 1
fi

echo "✓ Emscripten loaded: $(emcc --version | head -n 1)"
echo ""

# Get SQLite source
if [ ! -f "${SQLITE_DIR}/sqlite3.c" ]; then
    if [ -f "${SQLITE_PCL_RAW}/sqlite3.c" ]; then
        echo "Using SQLite from SQLitePCL.raw project..."
        mkdir -p "${SQLITE_DIR}"
        cp "${SQLITE_PCL_RAW}/sqlite3.c" "${SQLITE_DIR}/"
        cp "${SQLITE_PCL_RAW}/sqlite3.h" "${SQLITE_DIR}/"
        echo "✓ SQLite copied from SQLitePCL.raw"
    else
        echo "Downloading SQLite ${SQLITE_VERSION}..."

        mkdir -p "${SQLITE_DIR}"
        cd "${SQLITE_DIR}"

        # Download amalgamation
        SQLITE_URL="https://www.sqlite.org/${SQLITE_YEAR}/sqlite-amalgamation-${SQLITE_VERSION}.zip"
        echo "  URL: ${SQLITE_URL}"

        curl -L -o sqlite.zip "${SQLITE_URL}"
        unzip -q sqlite.zip
        mv sqlite-amalgamation-${SQLITE_VERSION}/* .
        rm -rf sqlite-amalgamation-${SQLITE_VERSION} sqlite.zip

        echo "✓ SQLite downloaded"
    fi
else
    echo "✓ SQLite already available"
fi

echo ""

# Verify our VFS tracking source
if [ ! -f "${SCRIPT_DIR}/src/vfs_tracking.c" ]; then
    echo "ERROR: VFS tracking source not found!"
    echo "Expected: ${SCRIPT_DIR}/src/vfs_tracking.c"
    exit 1
fi

echo "✓ VFS tracking source found"
echo ""

# Create build directory
echo "Creating build directory..."
mkdir -p "${BUILD_DIR}"
mkdir -p "${OUTPUT_DIR}"

# Compile flags matching e_sqlite3 builds (minimal omits to match packaged version)
echo "Setting up compile flags..."
CFLAGS=(
    -O3
    -DSQLITE_THREADSAFE=0
    -DSQLITE_ENABLE_FTS4
    -DSQLITE_ENABLE_FTS5
    -DSQLITE_ENABLE_JSON1
    -DSQLITE_ENABLE_RTREE
    -DSQLITE_ENABLE_SNAPSHOT
    -DSQLITE_ENABLE_COLUMN_METADATA
    -I"${SQLITE_DIR}"
    -I"${SCRIPT_DIR}/src"
)

echo ""
echo "Compiling sqlite3.c..."
emcc "${CFLAGS[@]}" \
    -c "${SQLITE_DIR}/sqlite3.c" \
    -o "${BUILD_DIR}/sqlite3.o"

echo "Compiling vfs_tracking.c..."
emcc "${CFLAGS[@]}" \
    -c "${SCRIPT_DIR}/src/vfs_tracking.c" \
    -o "${BUILD_DIR}/vfs_tracking.o"

echo ""
echo "Creating static library..."
emar rcs "${OUTPUT_DIR}/e_sqlite3.a" \
    "${BUILD_DIR}/sqlite3.o" \
    "${BUILD_DIR}/vfs_tracking.o"

echo ""

# Check output
if [ -f "${OUTPUT_DIR}/e_sqlite3.a" ]; then
    FILE_SIZE=$(du -h "${OUTPUT_DIR}/e_sqlite3.a" | cut -f1)
    echo "==================================="
    echo "✓ Build successful!"
    echo "==================================="
    echo ""
    echo "Output: ${OUTPUT_DIR}/e_sqlite3.a"
    echo "Size:   ${FILE_SIZE}"
    echo ""
    echo "The library includes:"
    echo "  • SQLite ${SQLITE_VERSION} (amalgamation)"
    echo "  • VFS tracking wrapper"
    echo "  • Dirty page bitmap tracking"
    echo ""
    echo "Next steps:"
    echo "  1. Update SQLiteNET.Opfs.csproj to reference this library"
    echo "  2. Implement C# P/Invoke wrappers (Interop/VfsInterop.cs)"
    echo "  3. Update OpfsStorageService for incremental sync"
    echo "  4. Test with demo application"
    echo ""
else
    echo "ERROR: Build failed - output file not found"
    exit 1
fi
