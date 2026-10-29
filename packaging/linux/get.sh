#!/bin/sh
# One-line install of the newest StreamLite for Linux:
#   curl -fsSL https://raw.githubusercontent.com/NermalYT/StreamLite/main/packaging/linux/get.sh | sh
# Downloads the release's Linux installer, checks it against SHA256SUMS.txt and runs it (it asks for your password
# once, to install what StreamLite needs from your distro). Options are passed on: ... | sh -s -- --system
set -e
repo=NermalYT/StreamLite
[ "$(uname -m)" = x86_64 ] || { echo "StreamLite for Linux needs a 64-bit Intel/AMD (x86_64) PC."; exit 1; }
get() { if command -v curl >/dev/null 2>&1; then curl -fsSL "$1"; else wget -qO- "$1"; fi; }
tag=$(get "https://api.github.com/repos/$repo/releases/latest" | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n1)
[ -n "$tag" ] || { echo "Couldn't find the newest release on GitHub."; exit 1; }
ver=${tag#v}
file="StreamLite-$ver-linux.sh"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
echo "Downloading StreamLite $ver..."
get "https://github.com/$repo/releases/download/$tag/$file" > "$tmp/$file"
get "https://github.com/$repo/releases/download/$tag/SHA256SUMS.txt" > "$tmp/SHA256SUMS.txt"
(cd "$tmp" && grep " $file\$" SHA256SUMS.txt | sha256sum -c -) || { echo "The download is damaged - try again."; exit 1; }
# Piped into sh, the installer reads the terminal (sudo asks for your password there).
if [ -t 1 ] && (exec </dev/tty) 2>/dev/null; then bash "$tmp/$file" "$@" </dev/tty; else bash "$tmp/$file" "$@"; fi
