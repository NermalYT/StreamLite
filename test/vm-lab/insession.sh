#!/bin/bash
# insession <command...>: runs a command as the test user inside the running desktop session (from SSH), with the
# session's display, Wayland, D-Bus and sound settings - taken from the desktop's own processes.
uid=$(id -u tester)
for p in gnome-shell plasmashell kwin_wayland cinnamon xfce4-session cosmic-comp cosmic-session; do
    pid=$(pgrep -u "$uid" -o -x "$p") && break
done
[ -n "$pid" ] || { echo "no desktop session running" >&2; exit 1; }
declare -A env
while IFS= read -r -d '' kv; do env[${kv%%=*}]=${kv#*=}; done < "/proc/$pid/environ"
# GNOME and Plasma hand DISPLAY/XAUTHORITY to the user's systemd after the shell starts.
while IFS= read -r kv; do env[${kv%%=*}]=${kv#*=}; done < <(XDG_RUNTIME_DIR=/run/user/$uid sudo -u tester XDG_RUNTIME_DIR=/run/user/$uid systemctl --user show-environment 2>/dev/null)
args=()
for k in DISPLAY WAYLAND_DISPLAY XAUTHORITY DBUS_SESSION_BUS_ADDRESS XDG_RUNTIME_DIR XDG_SESSION_TYPE XDG_CURRENT_DESKTOP \
         XDG_SESSION_DESKTOP XDG_DATA_DIRS XDG_CONFIG_DIRS DESKTOP_SESSION LANG PATH YDOTOOL_SOCKET; do
    [ -n "${env[$k]}" ] && args+=("$k=${env[$k]}")
done
[ -z "${env[XDG_RUNTIME_DIR]}" ] && args+=("XDG_RUNTIME_DIR=/run/user/$uid")
[ -z "${env[PATH]}" ] && args+=("PATH=/usr/local/bin:/usr/bin:/bin")
[ -f /run/ydotoold.sock ] || [ -S /run/ydotoold.sock ] && args+=("YDOTOOL_SOCKET=/run/ydotoold.sock")
if [ "$(id -u)" = "$uid" ]; then exec env -i HOME=/home/tester USER=tester "${args[@]}" "$@"
else exec sudo -u tester env -i HOME=/home/tester USER=tester "${args[@]}" "$@"; fi
