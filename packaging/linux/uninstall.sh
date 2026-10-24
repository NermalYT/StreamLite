#!/bin/sh
# Removes a StreamLite installed with install.sh (just for you, or with --system for everyone).
#   ./uninstall.sh              sudo ./uninstall.sh --system
#   add --settings to also delete your settings (~/.config/StreamLite)
set -e
data="${XDG_DATA_HOME:-$HOME/.local/share}"
case " $* " in *" --system "*)
    [ "$(id -u)" = 0 ] || { echo "Run: sudo $0 --system"; exit 1; }
    dest=/opt/streamlite bin=/usr/local/bin apps=/usr/share/applications icons=/usr/share/icons/hicolor/256x256/apps ;;
*)  dest="$data/streamlite" bin="$HOME/.local/bin" apps="$data/applications" icons="$data/icons/hicolor/256x256/apps" ;;
esac
if pgrep -x streamlite >/dev/null 2>&1; then
    echo "StreamLite is running: quit it first (tray icon > Quit), so it can put your sound devices back."
    exit 1
fi
# Leftovers from a crash: StreamLite's virtual devices.
if command -v pactl >/dev/null 2>&1; then
    LC_ALL=C pactl list short modules 2>/dev/null | grep streamlite_ | cut -f1 | while read -r m; do pactl unload-module "$m" || true; done
fi
rm -rf "$dest"
rm -f "$bin/streamlite" "$apps/streamlite.desktop" "$icons/streamlite.png" "$HOME/.config/autostart/streamlite.desktop"
case " $* " in *" --settings "*) rm -rf "${XDG_CONFIG_HOME:-$HOME/.config}/StreamLite"; echo "Settings removed." ;; esac
echo "StreamLite is removed."
