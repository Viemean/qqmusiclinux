#!/usr/bin/env bash
set -euo pipefail

# 入参：版本号、包构建号、目标架构 (amd64 / arm64)、.NET Runtime ID (RID)
VERSION="${1:-0.2.1}"
PKGREL="${2:-1}"
ARCH="${3:-amd64}"

case "$ARCH" in
    x86_64|amd64)
        ARCH="amd64"
        RID="${4:-linux-x64}"
        ;;
    aarch64|arm64)
        ARCH="arm64"
        RID="${4:-linux-arm64}"
        ;;
    *)
        ARCH="${3:-amd64}"
        RID="${4:-linux-x64}"
        ;;
esac

PKGNAME="qqmusic-tui"
OUTPUT_FILE="${PKGNAME}_${VERSION}-${PKGREL}_${ARCH}.deb"

BIN_PATH="bin/Release/net10.0/${RID}/publish/QQMusic.Tui"
if [ ! -f "$BIN_PATH" ]; then
    echo "==> Binary not found at $BIN_PATH, building native AOT binary for Debian ${ARCH} (${RID})..."
    dotnet publish -c Release -r "${RID}" QQMusic.Tui.csproj
else
    echo "==> Reusing existing binary at $BIN_PATH"
fi

if [ ! -f "$BIN_PATH" ]; then
    echo "Error: Binary not found at $BIN_PATH after publish."
    exit 1
fi

if ! command -v dpkg-deb >/dev/null 2>&1; then
    echo "Error: dpkg-deb not found. Please install dpkg (e.g. sudo apt install dpkg or yay -S dpkg)."
    exit 1
fi

echo "==> Assembling fakeroot deb directory..."
STAGE_DIR=$(mktemp -d /tmp/qqmusic_deb.XXXXXX)
trap 'rm -rf "$STAGE_DIR"' EXIT

mkdir -p "$STAGE_DIR/DEBIAN" "$STAGE_DIR/usr/bin"
cp "$BIN_PATH" "$STAGE_DIR/usr/bin/qqmusic-tui"
chmod 755 "$STAGE_DIR/usr/bin/qqmusic-tui"

if [ -d "www" ]; then
    mkdir -p "$STAGE_DIR/usr/share/qqmusic-tui/www"
    cp -r www/* "$STAGE_DIR/usr/share/qqmusic-tui/www/"
fi

# 复制 QAFP 官方听歌识曲运行时 (微型 Bionic sysroot、libMusicWrapper.so 及模型)
QAFP_SRC="Services/QqAudioRecognition/Runtime/qafp"
if [ ! -d "$QAFP_SRC" ] && [ -d "$HOME/.local/share/qqmusic-tui/qafp" ]; then
    QAFP_SRC="$HOME/.local/share/qqmusic-tui/qafp"
fi

if [ -d "$QAFP_SRC" ]; then
    echo "==> Packing QAFP audio recognition runtime from $QAFP_SRC..."
    mkdir -p "$STAGE_DIR/usr/share/qqmusic-tui/qafp"
    cp -r "$QAFP_SRC/." "$STAGE_DIR/usr/share/qqmusic-tui/qafp/"
    chmod 755 "$STAGE_DIR/usr/share/qqmusic-tui/qafp/qafp_runner" || true
    chmod 755 "$STAGE_DIR/usr/share/qqmusic-tui/qafp/sysroot/system/bin/linker64" || true
fi

# Debian Installed-Size 单位是 KiB
INSTALLED_SIZE=$(du -sk "$STAGE_DIR/usr" | awk '{print $1}')

DEBIAN_DEPS="libgstreamer1.0-0, gstreamer1.0-plugins-base, gstreamer1.0-plugins-good, gstreamer1.0-plugins-bad, libpulse0"
if [ "${ARCH}" = "amd64" ] || [ "${ARCH}" = "i386" ]; then
    DEBIAN_DEPS="${DEBIAN_DEPS}, qemu-user-static"
fi

echo "==> Generating DEBIAN/control metadata..."
cat << EOF > "$STAGE_DIR/DEBIAN/control"
Package: ${PKGNAME}
Version: ${VERSION}-${PKGREL}
Section: sound
Priority: optional
Architecture: ${ARCH}
Maintainer: Yuzuki <lxf74663@gmail.com>
Installed-Size: ${INSTALLED_SIZE}
Depends: ${DEBIAN_DEPS}
Recommends: gstreamer1.0-libav
Suggests: wl-clipboard, xclip
Homepage: https://github.com/Viemean/qqmusiclinux/tree/tui
Description: Linux terminal QQ Music player (.NET 10 Native AOT)
 A high-performance terminal QQ Music player written in C# (.NET 10 Native AOT),
 featuring rich TUI interface, local Web UI remote control, and lossless GStreamer playback.
EOF

echo "==> Packing Debian .deb package with dpkg-deb..."
dpkg-deb --build --root-owner-group "$STAGE_DIR" "$OUTPUT_FILE"

echo "==> Debian package generated successfully: ${OUTPUT_FILE}"
ls -lh "$OUTPUT_FILE"
