#!/bin/sh
# One-line install of the newest StreamLite for Linux, just for you (no password):
#   curl -fsSL https://raw.githubusercontent.com/NermalYT/StreamLite/main/packaging/linux/get.sh | sh
# Downloads the release tarball, checks it against SHA256SUMS.txt and runs its install.sh.
set -e
repo=NermalYT/StreamLite
[ "$(uname -m)" = x86_64 ] || { echo "StreamLite for Linux needs a 64-bit Intel/AMD (x86_64) PC."; exit 1; }
get() { if command -v curl >/dev/null 2>&1; then curl -fsSL "$1"; else wget -qO- "$1"; fi; }
tag=$(get "https://api.github.com/repos/$repo/releases/latest" | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n1)
[ -n "$tag" ] || { echo "Couldn't find the newest release on GitHub."; exit 1; }
ver=${tag#v}
file="streamlite-$ver-linux-x64.tar.gz"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
echo "Downloading StreamLite $ver..."
get "https://github.com/$repo/releases/download/$tag/$file" > "$tmp/$file"
get "https://github.com/$repo/releases/download/$tag/SHA256SUMS.txt" > "$tmp/SHA256SUMS.txt"
(cd "$tmp" && grep " $file\$" SHA256SUMS.txt | sha256sum -c -) || { echo "The download is damaged - try again."; exit 1; }
tar -xzf "$tmp/$file" -C "$tmp"
sh "$tmp/streamlite-$ver/install.sh"
