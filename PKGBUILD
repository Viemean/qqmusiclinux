# Maintainer: Yuzuki <lxf74663@gmail.com>

pkgname=qqmusic-tui-bin
_pkgname=qqmusic-tui
pkgver=0.2.0
pkgrel=1
pkgdesc="Linux terminal QQ Music player (.NET 10 Native AOT pre-built binary package)"
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

package() {
    # 如果本地已经有构建好的二进制产物则优先使用，否则从 Release 下载
    local _bin_src="${srcdir}/../bin/Release/net10.0/linux-x64/publish/QQMusic.Tui"
    local _www_src="${srcdir}/../bin/Release/net10.0/linux-x64/publish/www"

    install -dm755 "${pkgdir}/usr/bin"
    install -dm755 "${pkgdir}/usr/share/qqmusic-tui"

    if [ -f "${_bin_src}" ]; then
        install -Dm755 "${_bin_src}" "${pkgdir}/usr/bin/${_pkgname}"
    fi

    if [ -d "${_www_src}" ]; then
        cp -r "${_www_src}" "${pkgdir}/usr/share/qqmusic-tui/"
    fi

    local _qafp_src="${srcdir}/../Services/QqAudioRecognition/Runtime/qafp"
    if [ -d "${_qafp_src}" ]; then
        cp -r "${_qafp_src}" "${pkgdir}/usr/share/qqmusic-tui/"
        chmod 755 "${pkgdir}/usr/share/qqmusic-tui/qafp/qafp_runner" || true
        chmod 755 "${pkgdir}/usr/share/qqmusic-tui/qafp/sysroot/system/bin/linker64" || true
    fi
}
