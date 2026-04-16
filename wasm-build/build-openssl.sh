#!/usr/bin/env bash
# Build OpenSSL 3.x as a WASM static library using Emscripten.
# Output: $OUT_DIR/openssl/{lib/libcrypto.a, include/openssl/...}
#
# Usage:
#   ./build-openssl.sh            # skip if already built
#   ./build-openssl.sh --force    # rebuild from scratch
#
# Prerequisites: emcc (or EMSDK_PATH), wget/curl, tar, make, perl

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="${SCRIPT_DIR}/out/openssl"
OPENSSL_VERSION="${OPENSSL_VERSION:-3.3.2}"
OPENSSL_SRC_DIR="${SCRIPT_DIR}/src/openssl-${OPENSSL_VERSION}"
OPENSSL_TARBALL="${SCRIPT_DIR}/src/openssl-${OPENSSL_VERSION}.tar.gz"
OPENSSL_URL="https://github.com/openssl/openssl/releases/download/openssl-${OPENSSL_VERSION}/openssl-${OPENSSL_VERSION}.tar.gz"
JOBS="${JOBS:-$(nproc 2>/dev/null || echo 4)}"

# --- parse args ---
FORCE=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --force) FORCE=1; shift ;;
        *) echo "Unknown arg: $1" >&2; exit 1 ;;
    esac
done

# Activate Emscripten if not in PATH
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

EMCC_VERSION=$(emcc --version 2>/dev/null | head -1)
echo "Using emcc: ${EMCC_VERSION}"

# Skip if already built
if [[ "${FORCE}" -eq 0 && -f "${OUT_DIR}/lib/libcrypto.a" ]]; then
    echo "OpenSSL WASM already built at ${OUT_DIR}. Use --force to rebuild."
    exit 0
fi
[[ "${FORCE}" -eq 1 ]] && rm -rf "${OUT_DIR}"

mkdir -p "${SCRIPT_DIR}/src"

# Download OpenSSL source if not present
if [[ ! -d "${OPENSSL_SRC_DIR}" ]]; then
    if [[ ! -f "${OPENSSL_TARBALL}" ]]; then
        echo "Downloading OpenSSL ${OPENSSL_VERSION}..."
        if command -v wget &>/dev/null; then
            wget -q -O "${OPENSSL_TARBALL}" "${OPENSSL_URL}"
        else
            curl -fsSL -o "${OPENSSL_TARBALL}" "${OPENSSL_URL}"
        fi
    fi
    echo "Extracting OpenSSL..."
    tar -xzf "${OPENSSL_TARBALL}" -C "${SCRIPT_DIR}/src"
fi

# Build in a separate directory to avoid polluting the source tree
BUILD_DIR="${SCRIPT_DIR}/src/openssl-${OPENSSL_VERSION}-wasm-build"
rm -rf "${BUILD_DIR}"
cp -r "${OPENSSL_SRC_DIR}" "${BUILD_DIR}"

mkdir -p "${OUT_DIR}"

cd "${BUILD_DIR}"

echo "Configuring OpenSSL for WebAssembly..."
# linux-generic32: closest WASM target; emcc passed directly to avoid emconfigure path issues.
# Kept: AES-256-CBC, HMAC+PBKDF2 (SHA1/256/512), RAND, OBJ/EVP core, ERR stubs.
# Disabled: TLS stack, all asymmetric crypto, unused ciphers/hashes/KDFs, WASM-incompatible
# features (threads, asm, shared, stdio, secure-memory, atexit, rdrand).
# OPENSSL_SMALL_FOOTPRINT: smaller AES tables; -ffunction/data-sections: dead-code stripping.
    ./Configure linux-generic32 \
    no-asm \
    no-threads \
    no-shared \
    no-dso \
    no-module \
    no-makedepend \
    no-tests \
    no-ssl3 \
    no-tls \
    no-dtls \
    no-quic \
    no-hw \
    no-engine \
    no-ec \
    no-dh \
    no-dsa \
    no-async \
    no-sock \
    no-dgram \
    no-autoload-config \
    no-atexit \
    no-cmp \
    no-http \
    no-rfc3779 \
    no-multiblock \
    no-srtp \
    no-psk \
    no-nextprotoneg \
    no-sctp \
    no-ktls \
    no-ssl-trace \
    no-siv \
    no-deprecated \
    no-err \
    no-autoerrinit \
    no-filenames \
    no-stdio \
    no-ui-console \
    no-bf \
    no-cast \
    no-des \
    no-rc2 \
    no-rc4 \
    no-rc5 \
    no-idea \
    no-camellia \
    no-seed \
    no-aria \
    no-chacha \
    no-poly1305 \
    no-ocb \
    no-sm2 \
    no-sm3 \
    no-sm4 \
    no-cmac \
    no-md4 \
    no-mdc2 \
    no-whirlpool \
    no-rmd160 \
    no-blake2 \
    no-siphash \
    no-scrypt \
    no-argon2 \
    no-rdrand \
    no-secure-memory \
    no-trace \
    no-legacy \
    no-srp \
    no-comp \
    no-gost \
    no-ocsp \
    no-cms \
    no-ts \
    no-ct \
    --prefix="${OUT_DIR}" \
    CC=emcc \
    CXX=em++ \
    AR=emar \
    RANLIB=emranlib \
    CFLAGS="-Oz -ffunction-sections -fdata-sections -DOPENSSL_SMALL_FOOTPRINT"

echo "Building libcrypto..."
make -j"${JOBS}" build_libs 2>&1

echo "Installing headers and library..."
mkdir -p "${OUT_DIR}/include"
cp -r include/openssl "${OUT_DIR}/include/"
# Copy build-generated headers
if [[ -f "include/openssl/opensslconf.h" ]]; then
    cp "include/openssl/opensslconf.h" "${OUT_DIR}/include/openssl/"
fi
if [[ -f "include/openssl/configuration.h" ]]; then
    cp "include/openssl/configuration.h" "${OUT_DIR}/include/openssl/"
fi

mkdir -p "${OUT_DIR}/lib"
if [[ -f "libcrypto.a" ]]; then
    cp libcrypto.a "${OUT_DIR}/lib/"
elif [[ -f "libcrypto_static.a" ]]; then
    cp libcrypto_static.a "${OUT_DIR}/lib/libcrypto.a"
else
    emmake make install_dev DESTDIR="" 2>&1
fi

if [[ -f "${OUT_DIR}/lib/libcrypto.a" ]]; then
    SIZE=$(du -h "${OUT_DIR}/lib/libcrypto.a" | cut -f1)
    echo ""
    echo "✓ OpenSSL WASM build complete!"
    echo "  Library: ${OUT_DIR}/lib/libcrypto.a  (${SIZE})"
    echo "  Headers: ${OUT_DIR}/include/openssl/"
else
    echo "ERROR: libcrypto.a not found after build." >&2
    exit 1
fi
