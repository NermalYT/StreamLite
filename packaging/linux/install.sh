#!/bin/sh
# Installs StreamLite from this folder.
#   ./install.sh               just for you, in ~/.local (no password needed)
#   sudo ./install.sh --system for everyone, in /opt/streamlite
# Run ./uninstall.sh (or the copy in the install folder) to remove it again.
set -e
cd "$(dirname "$0")"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
# install-kind.txt tells StreamLite's updater how it was installed ("tar": it updates itself; for everyone it can't).
if [ "$1" = "--system" ]; then
    [ "$(id -u)" = 0 ] || { echo "For everyone, run: sudo ./install.sh --system"; exit 1; }
    dest=/opt/streamlite bin=/usr/local/bin apps=/usr/share/applications icons=/usr/share/icons/hicolor/256x256/apps kind=tar-system
else
    dest="$data/streamlite" bin="$HOME/.local/bin" apps="$data/applications" icons="$data/icons/hicolor/256x256/apps" kind=tar
fi
mkdir -p "$dest/ai" "$bin" "$apps" "$icons"
# "install" replaces files without writing into them, so a running StreamLite keeps working until it restarts.
install -m 755 streamlite uninstall.sh "$dest/"
install -m 644 ./*.so streamlite.png LICENSE THIRD-PARTY-NOTICES.md "$dest/"
install -m 644 ai/* "$dest/ai/"
echo "$kind" > "$dest/install-kind.txt"
ln -sf "$dest/streamlite" "$bin/streamlite"
install -m 644 streamlite.png "$icons/streamlite.png"
sed "s|^Exec=.*|Exec=\"$dest/streamlite\"|" streamlite.desktop > "$apps/streamlite.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$apps" 2>/dev/null || true

echo "StreamLite is installed in $dest"
# StreamLite talks to PipeWire / PulseAudio through pactl and libpulse.
missing=""
command -v pactl >/dev/null 2>&1 || missing="pactl"
{ ldconfig -p 2>/dev/null | grep -q libpulse-simple.so.0; } || [ -e /usr/lib/x86_64-linux-gnu/libpulse-simple.so.0 ] || [ -e /usr/lib64/libpulse-simple.so.0 ] || [ -e /usr/lib/libpulse-simple.so.0 ] || missing="$missing libpulse-simple"
if [ -n "$missing" ]; then
    . /etc/os-release 2>/dev/null || true
    case " $ID $ID_LIKE " in
        *" arch "*) hint="sudo pacman -S libpulse" ;;
        *" fedora "*|*" rhel "*) hint="sudo dnf install pulseaudio-utils pulseaudio-libs" ;;
        *" suse "*|*" opensuse "*) hint="sudo zypper install pulseaudio-utils libpulse0" ;;
        *" debian "*|*" ubuntu "*) hint="sudo apt install pulseaudio-utils libpulse0" ;;
        *) hint="install your distro's pulseaudio-utils (pactl) and libpulse packages" ;;
    esac
    echo "One more step - StreamLite needs$missing (works with PipeWire too): $hint"
fi
case ":$PATH:" in
    *":$bin:"*) echo "Start it from your app menu or run: streamlite" ;;
    *) echo "Start it from your app menu or run: $dest/streamlite" ;;
esac
