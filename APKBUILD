# Maintainer: Yuzuki <lxf74663@gmail.com>
pkgname=qqmusic-tui
pkgver=0.1.0
pkgrel=1
pkgdesc="Linux terminal QQ Music player (.NET 10 Native AOT)"
url="https://github.com/Viemean/qqmusiclinux/tree/tui"
arch="aarch64 x86_64"
license="MIT"
depends="gstreamer gst-plugins-base gst-plugins-good"
makedepends="dotnet10-sdk clang lld gstreamer-dev gst-plugins-base-dev"
options="!check !strip"

build() {
    local rid="linux-musl-arm64"
    if [ "$CARCH" = "x86_64" ]; then
        rid="linux-musl-x64"
    fi
    dotnet publish -c Release -r "$rid" QQMusic.Tui.csproj
}

package() {
    local rid="linux-musl-arm64"
    if [ "$CARCH" = "x86_64" ]; then
        rid="linux-musl-x64"
    fi

    install -Dm755 "bin/Release/net10.0/$rid/publish/QQMusic.Tui" "$pkgdir/usr/bin/qqmusic-tui"
    if [ -d "bin/Release/net10.0/$rid/publish/www" ]; then
        mkdir -p "$pkgdir/usr/share/qqmusic-tui/www"
        cp -r "bin/Release/net10.0/$rid/publish/www/"* "$pkgdir/usr/share/qqmusic-tui/www/"
    fi
}
