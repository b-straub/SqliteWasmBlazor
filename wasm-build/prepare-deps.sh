#!/usr/bin/env bash
# prepare-deps.sh — Clone and pin all external source dependencies needed for
# the SQLCipher WASM build.  Run this once before the build steps.
#
# Frozen dependency versions (current known-good state):
#   SQLite    github.com/sqlite/sqlite         tag  version-3.51.3  (a5333afb9a)
#   SQLCipher github.com/sqlcipher/sqlcipher   tag  v4.14.0         (778ab890)
#   OpenSSL   3.3.2  (downloaded automatically by build-openssl.sh)
#   Emscripten SDK 5.x — minimum 5.0.4  (install separately via emsdk)
#   @sqlite.org/sqlite-wasm npm package   3.51.2-build8  (declared in package.json)
#
# Usage:
#   cd wasm-build/
#   ./prepare-deps.sh            # clone if absent, verify if present
#   ./prepare-deps.sh --update   # force checkout of pinned versions even if present
#
# After this script succeeds, run the build steps:
#   ./build-openssl.sh
#   ./build-sqlcipher.sh
#   ./build-wasm.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
DEPS_ROOT="$(cd "${REPO_ROOT}/.." && pwd)"

# --- pinned versions ---
SQLITE_REPO="https://github.com/sqlite/sqlite"
SQLITE_TAG="version-3.51.3"
SQLITE_COMMIT="a5333afb9a"
SQLITE_DIR="${SQLITE_DIR:-${DEPS_ROOT}/sqlite}"

SQLCIPHER_REPO="https://github.com/sqlcipher/sqlcipher"
SQLCIPHER_TAG="v4.14.0"
SQLCIPHER_COMMIT="778ab890"
SQLCIPHER_DIR="${SQLCIPHER_DIR:-${DEPS_ROOT}/sqlcipher}"

# --- parse args ---
UPDATE=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --update) UPDATE=1; shift ;;
        *) echo "Unknown arg: $1" >&2; exit 1 ;;
    esac
done

# Helper: print a coloured status line
ok()   { echo "  [OK]   $*"; }
warn() { echo "  [WARN] $*"; }
info() { echo "         $*"; }

# Helper: clone a repo and check out a pinned tag, or verify/update an existing clone.
# Usage: setup_repo <display_name> <dir> <repo_url> <tag> <commit_prefix>
setup_repo() {
    local name="$1" dir="$2" repo="$3" tag="$4" commit="$5"

    echo ""
    echo "--- ${name} ---"

    if [[ ! -d "${dir}/.git" ]]; then
        echo "  Cloning ${repo} ..."
        git clone --depth 1 --branch "${tag}" "${repo}" "${dir}"
        ok "Cloned at tag ${tag}"
        return
    fi

    # Repo exists — check current state
    local current_commit
    current_commit="$(git -C "${dir}" rev-parse --short HEAD 2>/dev/null || echo 'unknown')"
    local current_tag
    current_tag="$(git -C "${dir}" describe --tags --exact-match HEAD 2>/dev/null || echo '')"

    if [[ "${current_commit}" == "${commit}"* || "${current_tag}" == "${tag}" ]]; then
        ok "Already at ${tag} (${current_commit})"
        return
    fi

    if [[ "${UPDATE}" -eq 1 ]]; then
        echo "  Fetching and checking out ${tag} ..."
        git -C "${dir}" fetch --depth 1 origin "refs/tags/${tag}:refs/tags/${tag}" 2>/dev/null \
            || git -C "${dir}" fetch origin 2>/dev/null
        git -C "${dir}" checkout "${tag}"
        ok "Checked out ${tag}"
    else
        warn "Repo at ${dir} is NOT at the pinned tag ${tag}."
        info "Current: ${current_commit}${current_tag:+ (${current_tag})}"
        info "Run with --update to checkout ${tag}, or set the *_DIR env var to a different path."
    fi
}

echo "=== Preparing dependencies for SQLCipher WASM build ==="
echo ""
echo "  Deps root : ${DEPS_ROOT}"
echo "  Pinned versions:"
echo "    SQLite    ${SQLITE_TAG}     (${SQLITE_COMMIT})  → ${SQLITE_DIR}"
echo "    SQLCipher ${SQLCIPHER_TAG}  (${SQLCIPHER_COMMIT}) → ${SQLCIPHER_DIR}"
echo "    OpenSSL   3.3.2  (downloaded by build-openssl.sh)"
echo "    Emscripten 5.x (min 5.0.4, install separately via emsdk)"
echo "    @sqlite.org/sqlite-wasm 3.51.2-build8 (npm — see package.json)"

setup_repo "SQLite" "${SQLITE_DIR}" "${SQLITE_REPO}" "${SQLITE_TAG}" "${SQLITE_COMMIT}"
setup_repo "SQLCipher" "${SQLCIPHER_DIR}" "${SQLCIPHER_REPO}" "${SQLCIPHER_TAG}" "${SQLCIPHER_COMMIT}"

echo ""
echo "=== Dependency check complete ==="
echo ""
echo "Next steps:"
echo "  cd ${SCRIPT_DIR}"
echo "  ./build-openssl.sh"
echo "  ./build-sqlcipher.sh"
echo "  ./build-wasm.sh"
