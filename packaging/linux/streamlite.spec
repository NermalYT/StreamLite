# StreamLite RPM from the prebuilt program (build-linux.sh passes the staged tree as %{stage}).
# A .NET single-file program must not be stripped, so no debug packages and no post-install processing.
%global debug_package %{nil}
%global __os_install_post %{nil}
%global __requires_exclude_from ^/opt/streamlite/.*$
%global __provides_exclude_from ^/opt/streamlite/.*$

Name:           streamlite
Version:        @VERSION@
Release:        1
Summary:        Clean mic, 3D game audio and headphone EQ for PipeWire / PulseAudio
License:        GPL-3.0-or-later
URL:            https://github.com/NermalYT/StreamLite
BuildArch:      x86_64
# Library names differ between Fedora and openSUSE, so depend on the libraries themselves.
Requires:       /usr/bin/pactl
Requires:       libpulse-simple.so.0()(64bit)
Requires:       libfontconfig.so.1()(64bit)
Requires:       libX11.so.6()(64bit)
Requires:       libICE.so.6()(64bit)
Requires:       libSM.so.6()(64bit)
Requires:       libssl.so.3()(64bit)
Recommends:     /usr/bin/notify-send
Recommends:     /usr/bin/pkexec

%description
StreamLite removes background noise from your microphone with AI (RNNoise or
DeepFilterNet 3) and gives Discord, OBS, games and calls a clean virtual mic.
It turns games' 7.1 surround into 3D sound for headphones (SADIE II measured
heads, true 360 degrees), corrects your headphones with AutoEq and switches
headphone profiles for games, music and chat apps by itself.

%install
cp -a %{stage}/. %{buildroot}/

%files
/opt/streamlite
/usr/bin/streamlite
/usr/share/applications/streamlite.desktop
/usr/share/icons/hicolor/256x256/apps/streamlite.png
%doc /usr/share/doc/streamlite
