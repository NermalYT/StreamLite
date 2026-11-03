# StreamLite real-desktop lab (Hyper-V)

The Docker lab (`test/linux-lab`) is one fake desktop. This lab is the real ones: each distro's own desktop
(Wayland, its portals and tray), real kernels (v4l2loopback, USB drivers) and this PC's own USB cameras and mics
handed in over USB/IP. Run `vmlab.ps1` from an elevated PowerShell (Hyper-V needs it).

| VM | Desktop | Stands in for |
|---|---|---|
| sl-ubuntu2604-gnome | Ubuntu 26.04 LTS, GNOME (Wayland) | the newest Ubuntu |
| sl-ubuntu2404-gnome | Ubuntu 24.04 LTS, GNOME (Wayland) | most Ubuntu users |
| sl-mint-cinnamon | Ubuntu 24.04 + Cinnamon (X11, LightDM, PipeWire) | Linux Mint 22 (no cloud image of Mint itself) |
| sl-fedora44-gnome | Fedora 44 Workstation, GNOME | GNOME's newest portals |
| sl-fedora44-kde | Fedora 44, Plasma 6 (Wayland) | KDE |
| sl-fedora44-cosmic | Fedora 44, COSMIC | Pop!_OS 24.04 (COSMIC) |
| sl-debian13-xfce | Debian 13, Xfce (X11) | Debian, lightweight desktops, old-style trays |
| sl-arch-kde | Arch, Plasma 6 | Arch / CachyOS / Manjaro (pacman, rolling) |
| sl-tumbleweed-kde | openSUSE Tumbleweed, Plasma 6 | openSUSE (zypper) |
| sl-bazzite-kde | Bazzite (KDE), Plasma 6, Steam | gaming PCs and image-based (rpm-ostree) systems |
| sl-win11 | Windows 11 Pro (unactivated), no GPU, no sound devices | a clean PC for MSI install/upgrade/uninstall, CPU-only AI |

Each VM has a `clean` checkpoint: the desktop as a user has it, StreamLite never installed (and nothing the
installer brings - Mono, ffmpeg, v4l2loopback - added). `reset` goes back to it in seconds.

```
.\vmlab.ps1 list
.\vmlab.ps1 reset sl-fedora44-kde
.\vmlab.ps1 put sl-fedora44-kde ..\..\dist\StreamLite-4.1.4-linux.sh
.\vmlab.ps1 run sl-fedora44-kde "bash StreamLite-4.1.4-linux.sh --no-start"
.\vmlab.ps1 run sl-fedora44-kde "insession streamlite"        # in the signed-in desktop session
.\vmlab.ps1 shot sl-fedora44-kde kde.png
.\vmlab.ps1 run sl-fedora44-kde "insession ydotool key 29:1 56:1 20:1 20:0 56:0 29:0"   # Ctrl+Alt+T on Wayland
usbipd list                                                     # bus IDs of this PC's USB devices (change on reboot)
.\vmlab.ps1 usb sl-fedora44-kde attach 3-7                      # eMeet C960 into the VM (gone from Windows meanwhile)
.\vmlab.ps1 usb sl-fedora44-kde detach 3-7
.\vmlab.ps1 run sl-win11 "Get-ComputerInfo | Select OsName"    # Windows: PowerShell Direct
```

- The test user is `tester` (sudo without a password). Its password and the lab's SSH key are in
  `C:\StreamLiteLab\keys` - made by the lab, used only by these VMs.
- `key` and `type` drive a VM's console (`key sl-x ctrl+alt+f3`, `type sl-x tester`) for when SSH isn't up yet;
  `checkpoint` saves a VM's current state as its `clean` one.
- `usb` refuses the Realtek USB Audio device (the PC's loud speakers). An attached mic/headset/camera is
  unavailable to Windows until `detach`.
- The VMs have no GPU: CUDA stays in the Docker lab (`$env:LAB_GPU=1`). They have no sound card either - PipeWire's
  own devices, StreamLite's virtual ones, and USB devices handed in are what they hear and play.
- VM disks, seeds, images and keys live in `C:\StreamLiteLab` (`$env:STREAMLITE_LAB` moves it). Keep VMs on an
  SSD: on a hard disk (D: here is an SMR drive) a single image conversion took over half an hour. `build` remakes a
  VM from the distro's cloud image (`C:\StreamLiteLab\images`), `remove` deletes one. Test models and datasets
  stay in `D:\StreamLiteLab` (see `MODELS.md` there).
- Bazzite has no cloud image: `bazzite-build.sh` (in WSL, as root) turns its container image into
  `C:\StreamLiteLab\bazzite\bazzite.vhdx` with `tester` and the lab key baked in. After `build sl-bazzite-kde`,
  sign in on its console once (`key sl-bazzite-kde ctrl+alt+f3`, then `type`/`key enter` for the name, password
  and `sudo systemctl enable --now sshd`), give `tester` passwordless sudo, run `setup.sh kde` over SSH, reboot,
  then `checkpoint sl-bazzite-kde`. Steam starts with the session and shows its first-run dialog (it waits for
  OK before downloading its client), as on a fresh Bazzite install.
- Desktops sign in on their own at 1920x1080 (the VM's video setting; the guest's preferred mode is otherwise
  1024x768), with first-login wizards (GNOME, Ubuntu's what's-new pages, COSMIC, Bazzite Portal) turned off.
  Arch's Plasma 6.8 signs in through Plasma Login Manager (enabled by setup.sh); Tumbleweed's SDDM needs an X server.
- `sl-win11` installs unattended from `C:\StreamLiteLab\iso\Win11_x64_en-us.iso` (Microsoft's English x64 ISO,
  SHA-256 `BD4307DF...EA650`).
