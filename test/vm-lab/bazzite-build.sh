#!/bin/bash
# Makes the Bazzite lab disk (C:\StreamLiteLab\bazzite\bazzite.vhdx): Bazzite has no cloud image, so its bootable
# container is turned into a disk by Fedora's bootc-image-builder, with the lab's test user and SSH key baked in.
# Run in WSL as root:  wsl -d Ubuntu -u root -- bash /mnt/c/.../test/vm-lab/bazzite-build.sh
# Needs podman and qemu-img (installed here if missing), ~25 GB free in WSL and ~10 GB on C:.
set -e
LAB=${STREAMLITE_LAB_WSL:-/mnt/c/StreamLiteLab}
OUT=$LAB/bazzite; mkdir -p "$OUT"
command -v podman >/dev/null || { apt-get update -qq; DEBIAN_FRONTEND=noninteractive apt-get install -y -qq podman; }
command -v qemu-img >/dev/null || DEBIAN_FRONTEND=noninteractive apt-get install -y -qq qemu-utils
# The config holds the test password: it stays in WSL's own disk, not in the repository or on C:.
cfg=/root/bazzite-config.toml
printf '[[customizations.user]]\nname = "tester"\npassword = "%s"\nkey = "%s"\ngroups = ["wheel"]\n\n[[customizations.filesystem]]\nmountpoint = "/"\nminsize = "60 GiB"\n' \
    "$(tr -d '\r\n' < "$LAB/keys/tester-password.txt")" "$(tr -d '\r\n' < "$LAB/keys/lab_ed25519.pub")" > $cfg
chmod 600 $cfg
podman pull -q ghcr.io/ublue-os/bazzite:stable
mkdir -p /root/bib-out
podman run --rm --privileged --pull=newer --security-opt label=type:unconfined_t \
    -v /root/bib-out:/output -v $cfg:/config.toml:ro -v /var/lib/containers/storage:/var/lib/containers/storage \
    quay.io/centos-bootc/bootc-image-builder:latest --type qcow2 --rootfs btrfs --use-librepo=True ghcr.io/ublue-os/bazzite:stable
qemu-img convert -O vhdx -o subformat=dynamic /root/bib-out/qcow2/disk.qcow2 "$OUT/bazzite.vhdx.part"
mv "$OUT/bazzite.vhdx.part" "$OUT/bazzite.vhdx"
echo "made $OUT/bazzite.vhdx"
