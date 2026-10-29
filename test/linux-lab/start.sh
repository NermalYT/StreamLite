#!/usr/bin/env bash
# The lab's desktop and sound: X on :1 (1600x900), a window manager, VNC (inside the container only) shown through
# noVNC on port 6080, a D-Bus session and PipeWire with the test devices.
set -u
mkdir -p "$XDG_RUNTIME_DIR" && chmod 700 "$XDG_RUNTIME_DIR"
Xvfb :1 -screen 0 1600x900x24 -nolisten tcp >/tmp/xvfb.log 2>&1 &
for _ in $(seq 50); do xdpyinfo >/dev/null 2>&1 && break; sleep 0.1; done
eval "$(dbus-launch --sh-syntax)"
echo "export DBUS_SESSION_BUS_ADDRESS='$DBUS_SESSION_BUS_ADDRESS' DISPLAY=:1 XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR" > /tmp/lab-env
fluxbox >/tmp/fluxbox.log 2>&1 &
pipewire >/tmp/pipewire.log 2>&1 &
sleep 0.5
wireplumber >/tmp/wireplumber.log 2>&1 &
pipewire-pulse >/tmp/pipewire-pulse.log 2>&1 &
for _ in $(seq 50); do pactl info >/dev/null 2>&1 && break; sleep 0.2; done
/usr/local/bin/lab-devices >/tmp/devices.log 2>&1
x11vnc -display :1 -forever -shared -nopw -localhost -rfbport 5900 -quiet >/tmp/x11vnc.log 2>&1 &
websockify --web /usr/share/novnc 6080 localhost:5900 >/tmp/novnc.log 2>&1 &
echo "lab ready"
exec tail -f /dev/null
