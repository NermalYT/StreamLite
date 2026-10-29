
---

### Which file do I need?

| Your system | File |
|---|---|
| Windows 10/11 | `StreamLite-*-x64.msi`. If SmartScreen warns you, click **More info → Run anyway**. |
| Linux (any distro) | `StreamLite-*-linux.sh`, then run `bash StreamLite-*-linux.sh` - it installs what StreamLite needs from your distro too |

`SHA256SUMS.txt` lists the checksums of every file. Already have StreamLite? It updates itself when it starts.
From 4.1.4 there are just these two installers: a portable (zip) copy of StreamLite moves to the MSI when it updates
(your settings stay). On Linux, coming from 4.1.2 or older (the .deb, .rpm, AppImage, tarball or Arch package)?
Run the `.sh` once - it replaces the old StreamLite, keeps your settings, and StreamLite updates itself after that.
New here? Read the [manual](https://nermalyt.github.io/StreamLite/). Something wrong? Use the
[bug report](https://github.com/NermalYT/StreamLite/issues/new?template=bug_report.yml).
