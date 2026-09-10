#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET_DIR="${1:-$HOME/.local/share/qqmusic-tui/qafp}"
SYSROOT_LIB="${TARGET_DIR}/sysroot/system/lib64"
if [ ! -d "${SYSROOT_LIB}" ]; then
  SYSROOT_LIB="/tmp/android_sysroot/system/lib64"
fi

echo "=== Building QAFP Runner ==="
clang --target=aarch64-linux-android -c -o "${SCRIPT_DIR}/entry.o" "${SCRIPT_DIR}/entry.s"
clang --target=aarch64-linux-android \
  -nostdinc -nostdlib \
  -fuse-ld=lld \
  -pie \
  -Wl,-dynamic-linker,/system/bin/linker64 \
  -Wl,-rpath,/system/lib64 \
  -L"${SYSROOT_LIB}" \
  -lc -ldl \
  -o "${SCRIPT_DIR}/qafp_runner" "${SCRIPT_DIR}/entry.o" "${SCRIPT_DIR}/qafp_runner.c"

rm -f "${SCRIPT_DIR}/entry.o"

mkdir -p "${TARGET_DIR}"
cp "${SCRIPT_DIR}/qafp_runner" "${TARGET_DIR}/"
chmod +x "${TARGET_DIR}/qafp_runner"

echo "QAFP Runner built and installed to: ${TARGET_DIR}/qafp_runner"
