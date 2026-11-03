#!/bin/bash
# Turns a distro's cloud image into the desktop a StreamLite user has (run once by cloud-init, as root, on the first
# boot). Only the desktop and the lab's own tools are added: what StreamLite's installer brings (Mono, ffmpeg,
# v4l2loopback...) is left out on purpose, so the .sh is tested the way users run it. The VM powers off at the end.
# $1 = desktop: gnome | kde | cinnamon | xfce | cosmic
DESK="$1"
set -eE
trap 'echo "FAILED at line $LINENO: $BASH_COMMAND" > /var/log/lab-setup.status' ERR
echo running > /var/log/lab-setup.status
. /etc/os-release
U=tester
optional() { for p in "$@"; do $INSTALL "$p" >/dev/null 2>&1 && echo "added $p" || echo "not available: $p"; done; }

case "$ID" in
ubuntu|debian)
    export DEBIAN_FRONTEND=noninteractive
    INSTALL="apt-get install -y -q"
    dpkg --configure -a   # after a cut-short run
    apt-get update -q
    apt-get -y -q -o Dpkg::Options::=--force-confold full-upgrade
    if [ "$ID" = ubuntu ]; then
        # The cloud kernel lacks the USB camera/sound/USB-IP drivers; linux-generic has them (and its headers).
        $INSTALL linux-generic linux-tools-generic linux-cloud-tools-generic hwdata
        case "$DESK" in
            gnome) $INSTALL ubuntu-desktop-minimal ;;
            cinnamon) $INSTALL cinnamon-desktop-environment lightdm slick-greeter pipewire-audio ;;  # Linux Mint 22's desktop
        esac
    else
        $INSTALL hyperv-daemons usbip hwdata
        case "$DESK" in
            xfce) $INSTALL task-xfce-desktop ;;
            gnome) $INSTALL task-gnome-desktop ;;
        esac
    fi
    $INSTALL openssh-server
    optional xdotool ydotool firefox
    ;;
fedora)
    INSTALL="dnf install -y -q"
    dnf upgrade -y -q
    case "$DESK" in
        gnome) $INSTALL @workstation-product-environment ;;
        kde) $INSTALL @kde-desktop-environment ;;
        cosmic) $INSTALL @cosmic-desktop-environment gdm ;;
    esac
    $INSTALL kernel-modules kernel-modules-extra hyperv-daemons usbip openssh-server
    optional xdotool ydotool firefox
    ;;
arch)
    INSTALL="pacman -S --needed --noconfirm"
    pacman-key --init; pacman-key --populate archlinux
    pacman -Syu --noconfirm
    $INSTALL pipewire pipewire-pulse wireplumber plasma-meta sddm konsole dolphin hyperv usbip openssh
    optional xdotool ydotool firefox
    systemctl enable sshd
    ;;
opensuse-tumbleweed)
    INSTALL="zypper --non-interactive install"
    zypper --non-interactive refresh
    # The minimal image's kernel-default-base has no USB camera/sound drivers. Swap it for kernel-default before
    # the dup: left installed, it makes a later dup stop and ask what to do with it.
    zypper --non-interactive install --force-resolution kernel-default
    if rpm -q kernel-default-base >/dev/null 2>&1; then zypper --non-interactive remove kernel-default-base; fi
    zypper --non-interactive dup
    $INSTALL -t pattern kde kde_plasma
    # (SDDM's greeter runs on X here; the KDE pattern alone doesn't bring an X server.)
    $INSTALL sddm xorg-x11-server hyper-v usbip openssh
    optional xdotool ydotool MozillaFirefox
    sed -i 's/^DISPLAYMANAGER=.*/DISPLAYMANAGER="sddm"/;s/^DISPLAYMANAGER_AUTOLOGIN=.*/DISPLAYMANAGER_AUTOLOGIN="tester"/' /etc/sysconfig/displaymanager 2>/dev/null || true
    systemctl enable sshd
    ;;
bazzite)
    # Image-based (rpm-ostree): it already ships Plasma, usbip and ydotool, so nothing is installed.
    INSTALL=true
    f=/home/$U/.config/autostart/bazzite-portal.desktop   # its welcome window, opened on every sign-in
    if [ -f $f ] && ! grep -q '^Hidden=true' $f; then echo Hidden=true >> $f; fi
    ;;
*) echo "unknown distro $ID"; false ;;
esac

# Hyper-V tells the host the VM's address (KVP), so the lab finds it without a fixed IP.
for s in hv-kvp-daemon hv_kvp_daemon hypervkvpd; do systemctl enable "$s" 2>/dev/null && break; done || true
systemctl enable ssh 2>/dev/null || systemctl enable sshd 2>/dev/null || true
systemctl set-default graphical.target

# Sign the test user in on boot, to the desktop under test, with no lock screen.
case "$DESK" in
gnome|cosmic)
    f=/etc/gdm3/custom.conf; [ -d /etc/gdm3 ] || f=/etc/gdm/custom.conf
    mkdir -p "$(dirname $f)"
    printf '[daemon]\nAutomaticLoginEnable=True\nAutomaticLogin=%s\n' $U > "$f"
    if [ "$DESK" = cosmic ]; then
        mkdir -p /var/lib/AccountsService/users
        printf '[User]\nSession=cosmic\n' > /var/lib/AccountsService/users/$U
        systemctl disable cosmic-greeter 2>/dev/null || true
        systemctl enable -f gdm
    fi
    mkdir -p /etc/dconf/profile /etc/dconf/db/local.d
    printf 'user-db:user\nsystem-db:local\n' > /etc/dconf/profile/user
    printf '[org/gnome/desktop/session]\nidle-delay=uint32 0\n\n[org/gnome/desktop/screensaver]\nlock-enabled=false\n' > /etc/dconf/db/local.d/00-streamlite-lab
    dconf update || true
    # No first-login wizards: GNOME's setup, Ubuntu's "what's new in <release>" pages, COSMIC's setup.
    install -d -o $U -g $U /home/$U/.config /home/$U/.config/gnome-initial-setup /home/$U/.config/autostart
    echo yes > /home/$U/.config/gnome-initial-setup-done
    touch /home/$U/.config/gnome-initial-setup/upgrade-$VERSION_ID-done
    printf '[Desktop Entry]\nType=Application\nName=COSMIC Initial Setup\nHidden=true\n' > /home/$U/.config/autostart/com.system76.CosmicInitialSetup.desktop
    chown -R $U: /home/$U/.config
    ;;
kde)
    # The Wayland session is plasma.desktop on most distros, plasmawayland.desktop on openSUSE.
    s=$(ls /usr/share/wayland-sessions 2>/dev/null | grep -m1 -E '^plasma(wayland)?\.desktop$' || true)
    mkdir -p /etc/sddm.conf.d
    printf '[Autologin]\nUser=%s\nSession=%s\n' $U "${s:-plasma.desktop}" > /etc/sddm.conf.d/autologin.conf
    if [ -d /etc/plasmalogin.conf.d ] || command -v plasmalogin >/dev/null; then
        # Plasma Login Manager (Plasma 6.6+) replaces SDDM; Fedora enables it, Arch leaves it off.
        mkdir -p /etc/plasmalogin.conf.d; cp /etc/sddm.conf.d/autologin.conf /etc/plasmalogin.conf.d/
        systemctl enable -f plasmalogin
    else
        systemctl enable -f sddm
    fi
    install -d -o $U -g $U /home/$U/.config
    printf '[Daemon]\nAutolock=false\nLockOnResume=false\n' > /home/$U/.config/kscreenlockerrc; chown $U: /home/$U/.config/kscreenlockerrc
    ;;
cinnamon|xfce)
    mkdir -p /etc/lightdm/lightdm.conf.d
    s=cinnamon; [ "$DESK" = xfce ] && s=xfce
    printf '[Seat:*]\nautologin-user=%s\nautologin-session=%s\n' $U $s > /etc/lightdm/lightdm.conf.d/50-streamlite-lab.conf
    groupadd -f autologin; usermod -aG autologin $U
    systemctl enable -f lightdm
    apt-get remove -y -q light-locker xfce4-screensaver >/dev/null 2>&1 || true
    ;;
esac
# (On image-based systems some groups live only in /usr/lib/group, which usermod can't add to.)
for g in audio video input autologin; do getent group $g >/dev/null && { usermod -aG $g $U || echo "not added to $g"; }; done

# 1920x1080 text console (the desktop's size comes from the VM's video setting, which vmlab sets).
VIDEO="video=hyperv_fb:1920x1080 video=Virtual-1:1920x1080"
if command -v rpm-ostree >/dev/null; then rpm-ostree kargs $(printf -- '--append-if-missing=%s ' $VIDEO)
elif command -v grubby >/dev/null; then grubby --update-kernel=ALL --args="$VIDEO"
elif [ -d /etc/default/grub.d ] && command -v update-grub >/dev/null; then
    echo "GRUB_CMDLINE_LINUX_DEFAULT=\"\$GRUB_CMDLINE_LINUX_DEFAULT $VIDEO\"" > /etc/default/grub.d/99-streamlite-lab.cfg; update-grub
elif [ -f /etc/default/grub ]; then
    grep -q hyperv_fb /etc/default/grub || sed -i "s|^GRUB_CMDLINE_LINUX_DEFAULT=\"|GRUB_CMDLINE_LINUX_DEFAULT=\"$VIDEO |" /etc/default/grub
    if command -v grub2-mkconfig >/dev/null; then grub2-mkconfig -o /boot/grub2/grub.cfg; else grub-mkconfig -o /boot/grub/grub.cfg; fi
fi
[ -f /etc/kernel/cmdline ] && command -v sdbootutil >/dev/null && { sed -i "s|\$| $VIDEO|" /etc/kernel/cmdline; sdbootutil update-all-entries || true; }

# Input for tests on Wayland (ydotool) through one shared socket.
if command -v ydotoold >/dev/null; then
    printf '[Unit]\nDescription=ydotool daemon (StreamLite lab)\n[Service]\nExecStart=%s --socket-path=/run/ydotoold.sock --socket-perm=0666\n[Install]\nWantedBy=multi-user.target\n' "$(command -v ydotoold)" > /etc/systemd/system/lab-ydotoold.service
    systemctl enable lab-ydotoold
    echo YDOTOOL_SOCKET=/run/ydotoold.sock >> /etc/environment
fi
echo vhci-hcd > /etc/modules-load.d/lab-usbip.conf

if [ -d /etc/cloud ]; then touch /etc/cloud/cloud-init.disabled; fi
echo "ok $DESK $(date -u +%FT%TZ)" > /var/log/lab-setup.status
