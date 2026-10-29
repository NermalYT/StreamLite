#!/usr/bin/env bash
# StreamLite for Linux - one installer for every distro (Ubuntu, Debian, Mint, Pop!_OS, Fedora, Nobara, Bazzite,
# openSUSE, Arch, Manjaro, EndeavourOS, CachyOS...). Run it:   bash StreamLite-@VERSION@-linux.sh
#
#   (no options)    install StreamLite for you (~/.local), with what it needs from your distro (asks for your password once)
#   --system        install for everyone (/opt/streamlite)
#   --update        install over the copy that's there (StreamLite's updater runs this); --wait PID waits for it to close
#   --no-start      don't start StreamLite afterwards        --minimized  start it in the background
#   --no-deps       skip installing what it needs from your distro
#   --uninstall     remove StreamLite (asks whether to keep your settings)
#
# What it installs from your distro: Mono (runs StreamLite), libgdiplus (drawing), pactl (PipeWire / PulseAudio), ffmpeg
# (cameras and video backgrounds), v4l-utils (camera settings), libXi (hotkeys) and v4l2loopback (StreamLite Camera).
set -u
VERSION="@VERSION@"
MODE=install SCOPE=user START=yes MIN="" DEPS=yes WAIT=""
while [ $# -gt 0 ]; do
    case "$1" in
        --system) SCOPE=system ;; --update) MODE=update ;; --uninstall) MODE=uninstall ;;
        --no-start) START=no ;; --minimized) MIN="--minimized" ;; --no-deps) DEPS=no ;;
        --wait) shift; WAIT="${1:-}" ;;
        -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    esac
    shift
done

say() { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m==>\033[0m %s\n' "$*" >&2; }
die() { printf '\033[1;31m==>\033[0m %s\n' "$*" >&2; exit 1; }
SUDO=""; [ "$(id -u)" -ne 0 ] && SUDO="sudo"
interactive() { [ -t 0 ] && [ -t 1 ]; }

# Where a copy is installed now (the launcher points there), else where this install goes.
existing() {
    for l in "$HOME/.local/bin/streamlite" /usr/local/bin/streamlite; do
        [ -f "$l" ] && sed -n 's/^APP="\(.*\)"$/\1/p' "$l" | head -n1 && return
    done
}
user_dirs() { APP="$HOME/.local/share/streamlite"; BIN="$HOME/.local/bin"; DESK="$HOME/.local/share/applications"; ICONS="$HOME/.local/share/icons/hicolor/256x256/apps"; AS=""; }
system_dirs() { APP="/opt/streamlite"; BIN="/usr/local/bin"; DESK="/usr/share/applications"; ICONS="/usr/share/icons/hicolor/256x256/apps"; AS="$SUDO"; }

if [ "$MODE" = uninstall ]; then
    cur="$(existing)"; [ -n "$cur" ] && [ -f "$cur/uninstall.sh" ] && exec bash "$cur/uninstall.sh"
    die "StreamLite isn't installed by this installer here."
fi

if [ "$MODE" = update ]; then
    cur="$(existing)"
    if [ "$cur" = /opt/streamlite ]; then system_dirs; else user_dirs; fi
    DEPS=no
    if [ -n "$WAIT" ]; then for _ in $(seq 600); do kill -0 "$WAIT" 2>/dev/null || break; sleep 0.2; done; fi
else
    if [ "$SCOPE" = system ]; then system_dirs; else user_dirs; fi
fi

# ---- what StreamLite needs from the distro
install_deps() {
    # (os-release read in a subshell: it has a VERSION of its own.)
    distro=$(. /etc/os-release 2>/dev/null; echo "${PRETTY_NAME:-}")
    say "Installing what StreamLite needs from ${distro:-your distro} (Mono, ffmpeg, PipeWire tools...)"
    if command -v apt-get >/dev/null; then
        $SUDO apt-get update -qq || true
        $SUDO env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq mono-runtime libmono-system-windows-forms4.0-cil libmono-system-web-extensions4.0-cil \
            libmono-system-drawing4.0-cil libmono-system-management4.0-cil libmono-system-io-compression4.0-cil libmono-system-io-compression-filesystem4.0-cil \
            libmono-microsoft-csharp4.0-cil libmono-system-xml4.0-cil libmono-system-core4.0-cil libgdiplus pulseaudio-utils ffmpeg v4l-utils libxi6 xdg-utils libnotify-bin fonts-dejavu-core
        $SUDO env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq v4l2loopback-dkms || warn "v4l2loopback isn't available - StreamLite Camera won't be offered"
    elif command -v dnf >/dev/null; then
        $SUDO dnf install -y -q mono-core mono-winforms mono-web libgdiplus pulseaudio-utils v4l-utils libXi xdg-utils libnotify dejavu-sans-fonts
        $SUDO dnf install -y -q ffmpeg || $SUDO dnf install -y -q ffmpeg-free
        $SUDO dnf install -y -q v4l2loopback || $SUDO dnf install -y -q akmod-v4l2loopback || warn "v4l2loopback isn't available (it's in RPM Fusion) - StreamLite Camera won't be offered"
    elif command -v pacman >/dev/null; then
        $SUDO pacman -S --needed --noconfirm mono libgdiplus libpulse ffmpeg v4l-utils libxi xdg-utils libnotify ttf-dejavu
        $SUDO pacman -S --needed --noconfirm v4l2loopback-dkms || warn "v4l2loopback isn't available - StreamLite Camera won't be offered"
    elif command -v zypper >/dev/null; then
        $SUDO zypper --non-interactive install mono-core mono-winforms mono-web libgdiplus0 pulseaudio-utils ffmpeg v4l-utils libXi6 xdg-utils libnotify-tools dejavu-fonts
        $SUDO zypper --non-interactive install v4l2loopback-kmp-default || warn "v4l2loopback isn't available - StreamLite Camera won't be offered"
    else
        warn "Unknown package manager: install Mono (with WinForms), libgdiplus, pactl, ffmpeg, v4l-utils and libXi yourself."
    fi
    # StreamLite Camera: the virtual camera at every start, under its name, for browsers and Discord too.
    if modinfo v4l2loopback >/dev/null 2>&1; then
        echo "v4l2loopback" | $SUDO tee /etc/modules-load.d/streamlite-camera.conf >/dev/null
        echo 'options v4l2loopback devices=1 exclusive_caps=1 card_label="StreamLite Camera"' | $SUDO tee /etc/modprobe.d/streamlite-camera.conf >/dev/null
        lsmod | grep -q '^v4l2loopback' || $SUDO modprobe v4l2loopback devices=1 exclusive_caps=1 card_label="StreamLite Camera" || true
    fi
}

if [ "$DEPS" = yes ]; then install_deps; fi
command -v mono >/dev/null || die "Mono isn't installed - StreamLite needs it (run this installer again with your password, or install mono yourself)."

# ---- the program
say "Installing StreamLite $VERSION into $APP"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
LINE=$(awk '/^__STREAMLITE_PAYLOAD__$/ { print NR + 1; exit }' "$0")
tail -n +"$LINE" "$0" | tar -xz -C "$TMP" || die "The installer is damaged - download it again."
# A running StreamLite quits first (it takes its sound devices down and gives your defaults back), then the files are
# replaced - never under a program still running from them.
if pgrep -f "$APP/StreamLite.exe" >/dev/null 2>&1; then
    mono "$APP/StreamLite.exe" --quit >/dev/null 2>&1 || true
    for _ in $(seq 50); do pgrep -f "$APP/StreamLite.exe" >/dev/null 2>&1 || break; sleep 0.2; done
    pkill -f "$APP/StreamLite.exe" 2>/dev/null || true; sleep 0.5
fi
$AS mkdir -p "$APP" "$BIN" "$DESK" "$ICONS"
$AS cp -a --remove-destination "$TMP/streamlite/." "$APP/"
echo sh | $AS tee "$APP/install-kind.txt" >/dev/null

# The command that starts it, the menu entry and the icon.
$AS tee "$BIN/streamlite" >/dev/null <<EOF
#!/usr/bin/env bash
APP="$APP"
export STREAMLITE_LAUNCHER="$BIN/streamlite"
exec mono "\$APP/StreamLite.exe" "\$@"
EOF
$AS chmod 755 "$BIN/streamlite"
$AS install -m 644 "$APP/streamlite.png" "$ICONS/streamlite.png"
$AS tee "$DESK/streamlite.desktop" >/dev/null <<EOF
[Desktop Entry]
Type=Application
Name=StreamLite
GenericName=Clean mic, 3D game audio and headphone EQ
Comment=Noise removal, 3D game audio, headphone EQ and an AI camera
Exec=$BIN/streamlite
Icon=streamlite
Terminal=false
Categories=AudioVideo;Audio;Utility;
StartupWMClass=StreamLite.exe
EOF

# The uninstaller. StreamLite's Settings > Uninstall runs it with --keep-settings or --delete-settings after asking in
# its own window; run by hand it asks here. Root's part (a system-wide install, the camera module settings) goes
# through sudo in a terminal, else the desktop's password prompt (pkexec).
$AS tee "$APP/uninstall.sh" >/dev/null <<EOF
#!/usr/bin/env bash
# Removes StreamLite (installed into $APP) and, if you say so, your settings.
# Options: --keep-settings | --delete-settings (no question then)
settings=ask
for o in "\$@"; do case "\$o" in --keep-settings) settings=keep ;; --delete-settings) settings=delete ;; esac; done
asroot() { if [ "\$(id -u)" -eq 0 ]; then "\$@"; elif [ -t 0 ] && command -v sudo >/dev/null; then sudo "\$@"; elif command -v pkexec >/dev/null; then pkexec "\$@"; else sudo "\$@"; fi; }
mono "$APP/StreamLite.exe" --quit 2>/dev/null || true
for _ in \$(seq 50); do pgrep -af "$APP/StreamLite.exe" | grep -qv -- "--uninstall" || break; sleep 0.2; done
if [ -w "$(dirname "$APP")" ]; then rm -rf "$APP" "$BIN/streamlite" "$DESK/streamlite.desktop" "$ICONS/streamlite.png"
else asroot rm -rf "$APP" "$BIN/streamlite" "$DESK/streamlite.desktop" "$ICONS/streamlite.png"; fi
rm -f "\$HOME/.config/autostart/streamlite.desktop"
# StreamLite Camera's module settings (the installer put them in /etc).
if [ -f /etc/modules-load.d/streamlite-camera.conf ] || [ -f /etc/modprobe.d/streamlite-camera.conf ]; then
    echo "Removing StreamLite Camera's module settings (your password may be asked for)."
    asroot rm -f /etc/modules-load.d/streamlite-camera.conf /etc/modprobe.d/streamlite-camera.conf || true
fi
if [ "\$settings" = ask ]; then
    read -r -p "Also delete your StreamLite settings (mic tuning, EQs, profiles)? Keep them to bring them back after reinstalling (Scan for settings). [y/N] " a
    case "\$a" in y|Y) settings=delete ;; *) settings=keep ;; esac
fi
if [ "\$settings" = delete ]; then rm -rf "\$HOME/.config/StreamLite"; echo "Settings deleted."; else echo "Settings kept in ~/.config/StreamLite."; fi
echo "StreamLite is removed."
EOF
$AS chmod 755 "$APP/uninstall.sh"
command -v update-desktop-database >/dev/null && $AS update-desktop-database -q "$DESK" 2>/dev/null || true
case ":$PATH:" in *":$BIN:"*) ;; *) [ "$SCOPE" = user ] && warn "$BIN isn't on your PATH yet - log out and in once (StreamLite is in your app menu either way)." ;; esac

say "StreamLite $VERSION is installed - it's in your app menu."
if [ "$START" = yes ]; then
    nohup "$BIN/streamlite" $MIN >/dev/null 2>&1 &
fi
exit 0
__STREAMLITE_PAYLOAD__
