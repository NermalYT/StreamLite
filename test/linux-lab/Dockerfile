# StreamLite's Linux test lab: a desktop you can watch and click in a browser (noVNC on 127.0.0.1:6080), PipeWire
# with test devices only (a test mic you can play recordings into, and test headphones that go nowhere - no sound
# ever reaches a real speaker). Build and start: test/linux-lab/lab.ps1 up
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
      xvfb x11vnc novnc websockify fluxbox xterm dbus-x11 x11-utils xdotool imagemagick \
      pipewire pipewire-pulse wireplumber pipewire-alsa pulseaudio-utils alsa-utils \
      libicu74 fontconfig fonts-dejavu-core libfontconfig1 libx11-6 libice6 libsm6 libxext6 libxrandr2 libxi6 libxcursor1 \
      v4l-utils libnotify-bin desktop-file-utils xdg-utils ca-certificates curl procps sudo less file \
    && rm -rf /var/lib/apt/lists/*
# A normal user (StreamLite never needs root to run; the installer asks for it only for a system install).
RUN useradd -m -s /bin/bash tester && echo "tester ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/tester
COPY start.sh /usr/local/bin/lab-start
COPY devices.sh /usr/local/bin/lab-devices
RUN chmod 755 /usr/local/bin/lab-start /usr/local/bin/lab-devices
USER tester
WORKDIR /home/tester
# WirePlumber quits without logind (no systemd in a container) unless its Bluetooth monitors are off.
RUN mkdir -p ~/.config/wireplumber/bluetooth.lua.d && printf 'bluez_monitor.enabled = false\nbluez_midi_monitor.enabled = false\n' > ~/.config/wireplumber/bluetooth.lua.d/80-lab.lua
ENV DISPLAY=:1 XDG_RUNTIME_DIR=/tmp/xdg-tester
EXPOSE 6080
CMD ["/usr/local/bin/lab-start"]
