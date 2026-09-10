#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# QQMusic-TUI 一键发布与 AUR 同步自动化脚本
# 流程：
# 1. 检查工作区与版本标签
# 2. 推送当前主分支与标签至 GitHub (触发 GitHub Actions Release 构建)
# 3. 轮询等待 GitHub Release 产物发布并下载 x86_64 / aarch64 包计算 SHA256
# 4. 自动切换至 aur-tui 分支，同步依赖与最新哈希，生成 .SRCINFO 并提交
# 5. 推送至 GitHub aur-tui 镜像与官方 AUR 仓库 (ssh://aur@aur.archlinux.org/qqmusic-tui-bin.git)
# 6. 安全切回原工作分支
# ==============================================================================

WORK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${WORK_DIR}"

CURRENT_BRANCH="$(git branch --show-current)"
if [ -z "${CURRENT_BRANCH}" ]; then
    echo "[错误] 无法获取当前 Git 分支名称" >&2
    exit 1
fi

# 检查工作区状态
if [ -n "$(git status -s)" ]; then
    echo "[错误] 当前工作区存在未提交的更改，请先提交或暂存后再执行发布脚本" >&2
    git status -s
    exit 1
fi

# 获取目标版本号 (支持通过入参指定，默认读取最新的 Git Tag 或 Program.cs)
VERSION="${1:-}"
if [ -z "${VERSION}" ]; then
    LATEST_TAG="$(git describe --tags --abbrev=0 2>/dev/null || echo "")"
    if [ -n "${LATEST_TAG}" ]; then
        VERSION="${LATEST_TAG#v}"
    else
        VERSION="$(grep -oP 'qqmusic-tui \K[0-9.]+' Program.cs | head -n 1 || echo "0.2.0")"
    fi
fi

TAG_NAME="v${VERSION}"
echo "=================================================="
echo "准备发布版本: ${VERSION} (Tag: ${TAG_NAME})"
echo "当前工作分支: ${CURRENT_BRANCH}"
echo "=================================================="

# 验证本地是否存在该 tag
if ! git rev-parse -q --verify "refs/tags/${TAG_NAME}" >/dev/null; then
    echo "[提示] 本地未找到标签 ${TAG_NAME}，正在自动创建附注标签..."
    git tag -a "${TAG_NAME}" -m "Release ${TAG_NAME} - Automated release"
fi

# 检查远程仓库配置
if ! git remote get-url origin >/dev/null 2>&1; then
    echo "[错误] 未检测到 origin 远程仓库配置" >&2
    exit 1
fi

if ! git remote get-url aur-tui >/dev/null 2>&1; then
    echo "[提示] 未检测到 aur-tui 远程仓库，正在配置默认 AUR 源..."
    git remote add aur-tui "ssh://aur@aur.archlinux.org/qqmusic-tui-bin.git"
fi

# 确保失败或退出时能恢复至初始分支
cleanup() {
    local exit_code=$?
    if [ "${exit_code}" -ne 0 ]; then
        echo -e "\n[中断] 脚本执行异常，正在恢复工作环境..."
    fi
    local now_branch
    now_branch="$(git branch --show-current 2>/dev/null || echo "")"
    if [ -n "${now_branch}" ] && [ "${now_branch}" != "${CURRENT_BRANCH}" ]; then
        echo "[恢复] 正在切换回分支: ${CURRENT_BRANCH}"
        git checkout -f "${CURRENT_BRANCH}" >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT

# 步骤 1: 推送当前分支与标签
echo -e "\n==> [1/5] 推送当前分支 (${CURRENT_BRANCH}) 与标签 (${TAG_NAME}) 到 origin..."
git push origin "${CURRENT_BRANCH}" --tags

# 步骤 2: 轮询等待 GitHub Release 产物构建完成
echo -e "\n==> [2/5] 等待 GitHub Actions 构建并发布 Release 产物..."
X86_URL="https://github.com/Viemean/qqmusiclinux/releases/download/${TAG_NAME}/qqmusic-tui-bin-${VERSION}-1-x86_64.pkg.tar.zst"
ARM_URL="https://github.com/Viemean/qqmusiclinux/releases/download/${TAG_NAME}/qqmusic-tui-bin-${VERSION}-1-aarch64.pkg.tar.zst"

TEMP_DIR="$(mktemp -d /tmp/qqmusic_release_XXXXXX)"
X86_FILE="${TEMP_DIR}/qqmusic-tui-bin-${VERSION}-1-x86_64.pkg.tar.zst"
ARM_FILE="${TEMP_DIR}/qqmusic-tui-bin-${VERSION}-1-aarch64.pkg.tar.zst"

wait_for_asset() {
    local url="$1"
    local file="$2"
    local name="$3"
    local max_retries=60 # 最多等待 10 分钟
    local count=0

    echo -n "等待 ${name} 产物就绪 "
    while [ $count -lt $max_retries ]; do
        local status_code
        status_code="$(curl -sIL -o /dev/null -w "%{http_code}" "$url" || true)"
        if [ "$status_code" = "200" ] || [ "$status_code" = "302" ]; then
            echo " [已就绪]"
            echo "正在下载 ${name} 并校验完整性..."
            if curl -fSL --progress-bar "$url" -o "$file"; then
                # 校验文件尺寸（预编译包应大于 3MB）
                local size
                size="$(stat -c%s "$file" 2>/dev/null || stat -f%z "$file" 2>/dev/null || echo 0)"
                if [ "$size" -gt 3145728 ]; then
                    return 0
                fi
            fi
        fi
        echo -n "."
        sleep 10
        count=$((count + 1))
    done

    echo -e "\n[错误] 超时未获取到 ${name} 发布产物 (${url})" >&2
    return 1
}

wait_for_asset "${X86_URL}" "${X86_FILE}" "x86_64"
wait_for_asset "${ARM_URL}" "${ARM_FILE}" "aarch64"

# 计算两端哈希
echo "计算 Release 产物 SHA-256 哈希值..."
SHA256_X86="$(sha256sum "${X86_FILE}" | awk '{print $1}')"
SHA256_ARM="$(sha256sum "${ARM_FILE}" | awk '{print $1}')"
echo "-> x86_64  SHA256: ${SHA256_X86}"
echo "-> aarch64 SHA256: ${SHA256_ARM}"

rm -rf "${TEMP_DIR}"

# 步骤 3 & 4: 使用临时 Git Worktree 更新 AUR PKGBUILD 与 .SRCINFO
echo -e "\n==> [3/5] 在独立工作树中检出 aur-tui 分支并同步远程状态..."
AUR_WT_DIR="$(mktemp -d /tmp/qqmusic_aur_wt_XXXXXX)"

cleanup_aur_wt() {
    if [ -d "${AUR_WT_DIR}" ]; then
        cd "${WORK_DIR}"
        git worktree remove --force "${AUR_WT_DIR}" 2>/dev/null || rm -rf "${AUR_WT_DIR}"
    fi
}
trap cleanup_aur_wt EXIT

if ! git rev-parse --verify aur-tui >/dev/null 2>&1; then
    git worktree add -B aur-tui "${AUR_WT_DIR}" origin/aur-tui
else
    git worktree add "${AUR_WT_DIR}" aur-tui
fi

cd "${AUR_WT_DIR}"
git pull origin aur-tui --ff-only 2>/dev/null || true

echo -e "\n==> [4/5] 正在生成 AUR PKGBUILD 与 .SRCINFO..."
cat << 'EOF' > PKGBUILD
# Maintainer: Yuzuki <lxf74663@gmail.com>

pkgname=qqmusic-tui-bin
_pkgname=qqmusic-tui
pkgver=__VERSION__
pkgrel=1
_upstream_pkgrel=1
pkgdesc="Linux terminal QQ Music player (.NET 10 Native AOT pre-built package)"
arch=('x86_64' 'aarch64')
url="https://github.com/Viemean/qqmusiclinux/tree/tui"
license=('MIT')
depends=(
    'gstreamer'
    'gst-plugins-base'
    'gst-plugins-good'
    'gst-plugins-bad'
    'libpulse'
)
depends_x86_64=(
    'qemu-user'
)

optdepends=(
    'gst-libav: additional audio codecs (AAC/M4A) support'
    'wl-clipboard: Wayland clipboard support for copying song links'
    'xclip: X11 clipboard support for copying song links'
)

provides=('qqmusic-tui')
conflicts=('qqmusic-tui')

source_x86_64=("${pkgname}-upstream-${pkgver}-${pkgrel}-x86_64.pkg.tar.zst::https://github.com/Viemean/qqmusiclinux/releases/download/v${pkgver}/${pkgname}-${pkgver}-${_upstream_pkgrel}-x86_64.pkg.tar.zst")
source_aarch64=("${pkgname}-upstream-${pkgver}-${pkgrel}-aarch64.pkg.tar.zst::https://github.com/Viemean/qqmusiclinux/releases/download/v${pkgver}/${pkgname}-${pkgver}-${_upstream_pkgrel}-aarch64.pkg.tar.zst")
sha256sums_x86_64=('__SHA256_X86__')
sha256sums_aarch64=('__SHA256_ARM__')

package() {
    cp -a "${srcdir}/usr" "${pkgdir}/"
}
EOF

sed -i "s/__VERSION__/${VERSION}/g" PKGBUILD
sed -i "s/__SHA256_X86__/${SHA256_X86}/g" PKGBUILD
sed -i "s/__SHA256_ARM__/${SHA256_ARM}/g" PKGBUILD

# 生成 .SRCINFO
echo "生成 .SRCINFO..."
makepkg --printsrcinfo > .SRCINFO

# 提交更新
git add PKGBUILD .SRCINFO
if git diff --staged --quiet; then
    echo "AUR PKGBUILD 已经是最新状态，无需提交"
else
    git commit -m "chore(release): bump to ${VERSION}-1"
fi

# 步骤 5: 推送 AUR
echo -e "\n==> [5/5] 推送至 AUR 与 GitHub 镜像分支..."
echo "推送至 GitHub origin/aur-tui..."
git push origin aur-tui

echo "推送至官方 AUR 仓库 (ssh://aur@aur.archlinux.org/qqmusic-tui-bin.git)..."
git push aur-tui aur-tui:master

# 清理临时工作树
cd "${WORK_DIR}"
git worktree remove --force "${AUR_WT_DIR}" 2>/dev/null || true

echo -e "\n=================================================="
echo "恭喜！v${VERSION} 已成功发布并同步至 Arch Linux AUR！"
echo "AUR 地址: https://aur.archlinux.org/packages/qqmusic-tui-bin"
echo "=================================================="
