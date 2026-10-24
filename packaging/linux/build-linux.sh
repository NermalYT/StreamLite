#!/usr/bin/env bash
# Builds the Linux release into dist/:
#   streamlite-<v>-linux-x64.tar.gz   any distro: folder with install.sh (no root needed)
#   streamlite_<v>_amd64.deb          Debian, Ubuntu, Mint, Pop!_OS, Zorin...
#   streamlite-<v>-1.x86_64.rpm       Fedora, openSUSE, Nobara, Bazzite (layered)... - needs rpmbuild
#   StreamLite-<v>-x86_64.AppImage    any distro, one file - downloads appimagetool
#   PKGBUILD                           Arch, Manjaro, EndeavourOS, CachyOS (makepkg -si)
# Usage: packaging/linux/build-linux.sh [version] [--no-publish]
#   --no-publish uses linux/publish as it is (for example published on Windows: dotnet publish linux -c Release -o linux/publish)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
ver="" publish=yes
for a in "$@"; do case "$a" in --no-publish) publish=no ;; *) ver="$a" ;; esac; done
[ -n "$ver" ] || ver="$(sed -n 's/.*const string Version = "\([0-9.]*\)".*/\1/p' "$root/linux/Program.cs" | head -n1)"
pub="$root/linux/publish"
dist="$root/dist"
maint="NermalYT <96622161+NermalYT@users.noreply.github.com>"
[ "$publish" = yes ] && dotnet publish "$root/linux" -c Release -o "$pub" -p:Version="$ver"
mkdir -p "$dist"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
echo "StreamLite $ver for Linux"

# The program folder every package installs: the program, its native libraries and the DeepFilterNet model.
files() {
    mkdir -p "$1/ai"
    install -m 755 "$pub/streamlite" "$1/streamlite"
    for so in libonnxruntime.so libSkiaSharp.so libHarfBuzzSharp.so; do install -m 644 "$pub/$so" "$1/$so"; done
    install -m 644 "$pub/ai/deepfilternet3.onnx" "$pub/ai/DEEPFILTERNET-NOTICE.txt" "$pub/ai/ONNXRUNTIME-LICENSE.txt" "$1/ai/"
}

# Menu entry, icon and licenses for system packages (deb / rpm), installed into /opt/streamlite + /usr.
system_tree() {
    files "$1/opt/streamlite"
    echo "$2" > "$1/opt/streamlite/install-kind.txt"
    mkdir -p "$1/usr/bin" "$1/usr/share/applications" "$1/usr/share/icons/hicolor/256x256/apps" "$1/usr/share/doc/streamlite"
    ln -s /opt/streamlite/streamlite "$1/usr/bin/streamlite"
    install -m 644 "$here/streamlite.desktop" "$1/usr/share/applications/streamlite.desktop"
    install -m 644 "$root/linux/streamlite.png" "$1/usr/share/icons/hicolor/256x256/apps/streamlite.png"
    install -m 644 "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$1/usr/share/doc/streamlite/"
}

# ---- tarball + install.sh
t="$work/streamlite-$ver"
files "$t"
install -m 755 "$here/install.sh" "$here/uninstall.sh" "$t/"
install -m 644 "$here/streamlite.desktop" "$root/linux/streamlite.png" "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$t/"
cat > "$t/README.txt" <<EOF
StreamLite $ver for Linux - clean mic, 3D game audio and headphone EQ for PipeWire / PulseAudio

  ./install.sh                install just for you (no password)
  sudo ./install.sh --system  install for everyone (/opt/streamlite)
  ./uninstall.sh              remove it again

Manual: https://nermalyt.github.io/StreamLite/
EOF
tar -C "$work" --owner=0 --group=0 --numeric-owner -czf "$dist/streamlite-$ver-linux-x64.tar.gz" "streamlite-$ver"
echo "  built streamlite-$ver-linux-x64.tar.gz"

# ---- .deb
d="$work/deb"
system_tree "$d" deb
mkdir -p "$d/DEBIAN"
cat > "$d/DEBIAN/control" <<EOF
Package: streamlite
Version: $ver
Section: sound
Priority: optional
Architecture: amd64
Maintainer: $maint
Installed-Size: $(du -sk "$d" | cut -f1)
Depends: libc6 (>= 2.28), libstdc++6, libgcc-s1 | libgcc1, zlib1g, libssl3 | libssl3t64 | libssl1.1, libfontconfig1, libx11-6, libice6, libsm6, libpulse0, pulseaudio-utils
Recommends: libnotify-bin, pkexec | policykit-1
Homepage: https://github.com/NermalYT/StreamLite
Description: clean mic, 3D game audio and headphone EQ for PipeWire / PulseAudio
 StreamLite removes background noise from your microphone with AI (RNNoise or
 DeepFilterNet 3) and gives Discord, OBS, games and calls a clean virtual mic.
 It turns games' 7.1 surround into 3D sound for headphones (SADIE II measured
 heads, true 360 degrees), corrects your headphones with AutoEq and switches
 headphone profiles for games, music and chat apps by itself.
EOF
dpkg-deb --root-owner-group -Zxz --build "$d" "$dist/streamlite_${ver}_amd64.deb" >/dev/null
echo "  built streamlite_${ver}_amd64.deb"

# ---- .rpm
if command -v rpmbuild >/dev/null 2>&1; then
    r="$work/rpmbuild"
    mkdir -p "$r/SPECS"
    system_tree "$work/rpmroot" rpm
    sed "s/@VERSION@/$ver/" "$here/streamlite.spec" > "$r/SPECS/streamlite.spec"
    rpmbuild -bb --quiet --define "_topdir $r" --define "stage $work/rpmroot" "$r/SPECS/streamlite.spec"
    cp "$r"/RPMS/x86_64/streamlite-"$ver"-1.x86_64.rpm "$dist/"
    echo "  built streamlite-$ver-1.x86_64.rpm"
else
    echo "  skipped the .rpm (rpmbuild isn't installed)"
fi

# ---- AppImage
a="$work/StreamLite.AppDir"
files "$a/usr/lib/streamlite"
cat > "$a/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/streamlite/streamlite" "$@"
EOF
chmod 755 "$a/AppRun"
install -m 644 "$here/streamlite.desktop" "$a/streamlite.desktop"
install -m 644 "$root/linux/streamlite.png" "$a/streamlite.png"
ln -s streamlite.png "$a/.DirIcon"
tool="${APPIMAGETOOL:-}"
if [ -z "$tool" ]; then
    tool="$work/appimagetool"
    curl -fsSL -o "$tool" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage || tool=""
    [ -n "$tool" ] && chmod 755 "$tool"
fi
if [ -n "$tool" ]; then
    rm -f "$dist/StreamLite-$ver-x86_64.AppImage"
    ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream "$a" "$dist/StreamLite-$ver-x86_64.AppImage" >"$work/appimagetool.log" 2>&1 || { cat "$work/appimagetool.log"; exit 1; }
    echo "  built StreamLite-$ver-x86_64.AppImage"
else
    echo "  skipped the AppImage (couldn't download appimagetool)"
fi

# ---- Arch PKGBUILD (installs the tarball above)
sum="$(sha256sum "$dist/streamlite-$ver-linux-x64.tar.gz" | cut -d' ' -f1)"
sed -e "s/@VERSION@/$ver/" -e "s/@SHA256@/$sum/" "$here/PKGBUILD.in" > "$dist/PKGBUILD"
echo "  built PKGBUILD"
