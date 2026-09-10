#!/usr/bin/env bash
set -euo pipefail

# 入参：版本号、包构建号、目标架构、.NET Runtime ID (RID)
VERSION="${1:-0.2.1}"
PKGREL="${2:-1}"
ARCH="${3:-x86_64}"
RID="${4:-linux-x64}"
PKGNAME="qqmusic-tui-bin"
OUTPUT_FILE="${PKGNAME}-${VERSION}-${PKGREL}-${ARCH}.pkg.tar.zst"

echo "==> Building native AOT binary for ${ARCH} (${RID})..."
dotnet publish -c Release -r "${RID}" QQMusic.Tui.csproj

BIN_PATH="bin/Release/net10.0/${RID}/publish/QQMusic.Tui"
if [ ! -f "$BIN_PATH" ]; then
    echo "Error: Binary not found at $BIN_PATH"
    exit 1
fi

echo "==> Assembling fakeroot package directory..."
STAGE_DIR=$(mktemp -d /tmp/qqmusic_tui_pkg.XXXXXX)
trap 'rm -rf "$STAGE_DIR"' EXIT

mkdir -p "$STAGE_DIR/usr/bin"
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

# 计算已安装文件总大小（字节）
INSTALLED_SIZE=$(du -sb "$STAGE_DIR/usr" | awk '{print $1}')
BUILD_DATE=$(date +%s)

echo "==> Generating .PKGINFO metadata..."
cat << EOF > "$STAGE_DIR/.PKGINFO"
pkgname = ${PKGNAME}
pkgbase = ${PKGNAME}
pkgver = ${VERSION}-${PKGREL}
pkgdesc = Linux terminal QQ Music player (.NET 10 Native AOT pre-built package)
url = https://github.com/Viemean/qqmusiclinux/tree/tui
builddate = ${BUILD_DATE}
packager = Yuzuki <lxf74663@gmail.com>
size = ${INSTALLED_SIZE}
arch = ${ARCH}
license = MIT
depend = gstreamer
depend = gst-plugins-base
depend = gst-plugins-good
depend = gst-plugins-bad
depend = libpulse
$( [ "$ARCH" = "x86_64" ] && echo "depend = qemu-user" )
optdepend = gst-libav: additional audio codecs (AAC/M4A) support
optdepend = wl-clipboard: Wayland clipboard support for copying song links
optdepend = xclip: X11 clipboard support for copying song links
provides = qqmusic-tui
conflict = qqmusic-tui
EOF
# 移除可能的空白行
sed -i '/^[[:space:]]*$/d' "$STAGE_DIR/.PKGINFO"

echo "==> Compressing Arch Linux package with zstd..."
tar -C "$STAGE_DIR" -c --zstd -f "$OUTPUT_FILE" .PKGINFO usr

echo "==> Package generated successfully: ${OUTPUT_FILE}"
ls -lh "$OUTPUT_FILE"
