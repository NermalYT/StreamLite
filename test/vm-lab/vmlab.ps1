# StreamLite's real-desktop Linux lab: Hyper-V VMs with each distro's own desktop (Wayland, portals, trays, PipeWire
# or PulseAudio), and this PC's USB cameras/mics passed in over USB/IP. Needs an elevated PowerShell (Hyper-V).
#   .\vmlab.ps1 list                         the lab's VMs, their state and address
#   .\vmlab.ps1 build <vm[,vm]|all>          makes a VM from its distro's cloud image, then saves the "clean" checkpoint
#   .\vmlab.ps1 start|stop <vm>              boots it (signed in to the desktop) / shuts it down
#   .\vmlab.ps1 reset <vm>                   back to "clean" (StreamLite never installed), then boots it
#   .\vmlab.ps1 run <vm> <command...>        runs a command over SSH as the test user ("insession <cmd>": in the desktop)
#   .\vmlab.ps1 put <vm> <file> [dest]       copies a file in (default: the test user's home)
#   .\vmlab.ps1 get <vm> <path> [dest]       copies a file out
#   .\vmlab.ps1 shot <vm> [file.png]         a picture of the VM's screen (from Hyper-V; works on any desktop)
#   .\vmlab.ps1 usb <vm> attach|detach <busid>   hands a USB device (usbipd list) to the VM, or back to Windows
#   .\vmlab.ps1 key <vm> ctrl+alt+f3 enter   presses keys on the VM's console; type <vm> <text> types text
#   .\vmlab.ps1 checkpoint <vm>              shuts it down and saves it as the new "clean"
# Everything the lab makes lives in C:\StreamLiteLab (images, VMs, keys) - on the NVMe drive: VMs on the hard disk D:
# crawl; $env:STREAMLITE_LAB moves it. (The voice/3D-audio models and datasets are in D:\StreamLiteLab.)
param([Parameter(Position = 0)][string]$What = "list", [Parameter(Position = 1)][string]$Vm,
      [Parameter(Position = 2, ValueFromRemainingArguments = $true)][string[]]$Rest)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$lab = if ($env:STREAMLITE_LAB) { $env:STREAMLITE_LAB } else { "C:\StreamLiteLab" }
$key = "$lab\keys\lab_ed25519"

# name = image, desktop. Mint 22 has no cloud image: Ubuntu 24.04 (its base) with Cinnamon stands in for it.
$Vms = [ordered]@{
    "sl-ubuntu2604-gnome" = "ubuntu-2604", "gnome"
    "sl-ubuntu2404-gnome" = "ubuntu-2404", "gnome"
    "sl-mint-cinnamon"    = "ubuntu-2404", "cinnamon"
    "sl-fedora44-gnome"   = "fedora-44", "gnome"
    "sl-fedora44-kde"     = "fedora-44", "kde"
    "sl-fedora44-cosmic"  = "fedora-44", "cosmic"
    "sl-debian13-xfce"    = "debian-13", "xfce"
    "sl-arch-kde"         = "arch", "kde"
    "sl-tumbleweed-kde"   = "opensuse-tw", "kde"
    # Bazzite (Fedora Atomic: read-only /usr, rpm-ostree, Flatpak) has no cloud image: its bootable container is made
    # into a disk by bootc-image-builder (bazzite-build.sh, in WSL). The test user and SSH key are baked in.
    "sl-bazzite-kde"      = "bazzite", "bazzite"
    # A clean Windows 11 (unactivated, from Microsoft's ISO in $lab\iso): no NVIDIA card and no sound devices, for
    # MSI install/upgrade/uninstall and the CPU-only paths. Reached with PowerShell Direct instead of SSH.
    "sl-win11"            = "Win11_x64_en-us.iso", "windows"
}

function Is-Windows([string]$name) { $Vms.Contains($name) -and $Vms[$name][1] -eq "windows" }

function Get-Cred
{
    $pw = ConvertTo-SecureString (Get-Content "$lab\keys\tester-password.txt" -Raw).Trim() -AsPlainText -Force
    New-Object Management.Automation.PSCredential("tester", $pw)
}

function Need-Vm { if (-not $Vm -or -not (Get-VM -Name $Vm -ErrorAction SilentlyContinue)) { throw "No lab VM '$Vm' (.\vmlab.ps1 list)" } }

function Get-Ip([string]$name, [int]$waitSeconds = 0)
{
    $until = (Get-Date).AddSeconds($waitSeconds)
    do {
        $nic = Get-VMNetworkAdapter -VMName $name
        $ip = $nic.IPAddresses | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' } | Select-Object -First 1
        if ($ip) { return $ip }
        # Before the guest's Hyper-V tools run (during setup): the host's neighbour table, by the VM's MAC address.
        $mac = ($nic.MacAddress -split '(..)' | Where-Object { $_ }) -join '-'
        $ip = Get-NetNeighbor -InterfaceAlias "vEthernet (Default Switch)" -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.LinkLayerAddress -eq $mac -and $_.State -ne "Unreachable" } | Select-Object -First 1 -ExpandProperty IPAddress
        if ($ip) { return $ip }
        Start-Sleep -Seconds 3
    } while ((Get-Date) -lt $until)
    $null
}

function Ssh-Args([string]$ip)
{
    @("-i", $key, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=NUL",
      "-o", "LogLevel=ERROR", "-o", "ConnectTimeout=8", "tester@$ip")
}

function Invoke-Vm([string]$name, [string]$command)
{
    # Just after a boot the neighbour table can still hold the VM's previous address: retry until SSH answers.
    $ip = Wait-Ssh $name 180
    if (-not $ip) { throw "$name doesn't answer SSH (is it running?)" }
    $ErrorActionPreference = "Continue"   # (PowerShell 5.1 would stop at the command's first line of stderr)
    & ssh.exe @(Ssh-Args $ip) $command
}

function Wait-Ssh([string]$name, [int]$seconds)
{
    # Returns the address that answered, or $null.
    $ErrorActionPreference = "Continue"
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) {
        $ip = Get-Ip $name 0
        if ($ip) { & ssh.exe @(Ssh-Args $ip) "true" 2>$null; if ($LASTEXITCODE -eq 0) { return $ip } }
        Start-Sleep -Seconds 4
    }
    $null
}

function Wait-Direct([string]$name, [int]$seconds)
{
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) {
        try { Invoke-Command -VMName $name -Credential (Get-Cred) { 1 } -ErrorAction Stop | Out-Null; return $true } catch { Start-Sleep -Seconds 5 }
    }
    $false
}

function Wait-Up([string]$name, [int]$seconds) { if (Is-Windows $name) { Wait-Direct $name $seconds } else { Wait-Ssh $name $seconds } }

function New-LabKeys
{
    New-Item -ItemType Directory -Force "$lab\keys" | Out-Null
    if (-not (Test-Path $key)) {
        & ssh-keygen.exe -q -t ed25519 -N '""' -C "streamlite-lab" -f $key | Out-Null
        # OpenSSH refuses a private key others can read.
        icacls $key /inheritance:r /grant:r "${env:USERNAME}:F" | Out-Null
    }
    $pw = "$lab\keys\tester-password.txt"
    if (-not (Test-Path $pw)) {
        $chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $b = New-Object byte[] 16; $rng.GetBytes($b)
        [IO.File]::WriteAllText($pw, (-join ($b | ForEach-Object { $chars[$_ % $chars.Length] })))
    }
}

function Write-Lf([string]$path, [string]$text) { [IO.File]::WriteAllText($path, $text.Replace("`r`n", "`n"), (New-Object System.Text.UTF8Encoding $false)) }

function Invoke-Tools([string]$what)
{
    # qemu-img and xorriso in a small container (streamlite-vmtools); they talk on stderr, which PowerShell 5.1 would
    # otherwise turn into an error.
    $ErrorActionPreference = "Continue"
    if (-not (docker images -q streamlite-vmtools 2>$null)) {
        "FROM alpine:3.22`nRUN apk add --no-cache qemu-img xorriso`n" | docker build -q -t streamlite-vmtools - 2>&1 | Out-Null
    }
    $out = docker run --rm -v "${lab}:/lab" streamlite-vmtools sh -c $what 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Failed: $what`n$($out -join "`n")" }
}

function New-Seed([string]$name, [string]$desk)
{
    $dir = "$lab\seeds\$name"; New-Item -ItemType Directory -Force $dir | Out-Null
    $pub = (Get-Content "$key.pub" -Raw).Trim()
    $pw = (Get-Content "$lab\keys\tester-password.txt" -Raw).Trim()
    # The scripts go in base64: no YAML quoting, and CRLF from a Windows checkout can't reach bash.
    $setup = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Content "$here\setup.sh" -Raw).Replace("`r`n", "`n")))
    $insession = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Content "$here\insession.sh" -Raw).Replace("`r`n", "`n")))
    Write-Lf "$dir\user-data" @"
#cloud-config
hostname: $name
users:
  - name: tester
    gecos: StreamLite Tester
    sudo: ALL=(ALL) NOPASSWD:ALL
    shell: /bin/bash
    lock_passwd: false
    plain_text_passwd: $pw
    ssh_authorized_keys:
      - $pub
ssh_pwauth: false
write_files:
  - path: /root/lab-setup.sh
    permissions: '0755'
    encoding: b64
    content: $setup
  - path: /usr/local/bin/insession
    permissions: '0755'
    encoding: b64
    content: $insession
runcmd:
  - [sh, -c, "bash /root/lab-setup.sh $desk > /var/log/lab-setup.log 2>&1"]
power_state:
  mode: poweroff
  timeout: 60
  condition: true
"@
    Write-Lf "$dir\meta-data" "instance-id: $name-1`nlocal-hostname: $name`n"
    Remove-Item "$dir\seed.iso" -ErrorAction SilentlyContinue
    Invoke-Tools "cd /lab/seeds/$name && xorriso -as mkisofs -quiet -o seed.iso -V cidata -J -R user-data meta-data"
    "$dir\seed.iso"
}

function Build-WindowsVm([string]$name, [string]$iso)
{
    if (-not (Test-Path "$lab\iso\$iso")) { throw "Windows ISO missing: $lab\iso\$iso" }
    $dir = "$lab\seeds\$name"; New-Item -ItemType Directory -Force $dir | Out-Null
    $pw = [Security.SecurityElement]::Escape((Get-Content "$lab\keys\tester-password.txt" -Raw).Trim())
    # Unattended setup: whole disk, Windows 11 Pro (Microsoft's generic install key picks the edition; it doesn't
    # activate), a local test user signed in automatically, no sleep, then off - the lab saves "clean" from there.
    Write-Lf "$dir\autounattend.xml" @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
  <settings pass="windowsPE">
    <component name="Microsoft-Windows-International-Core-WinPE" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <SetupUILanguage><UILanguage>en-US</UILanguage></SetupUILanguage>
      <InputLocale>en-US</InputLocale><SystemLocale>en-US</SystemLocale><UILanguage>en-US</UILanguage><UserLocale>en-US</UserLocale>
    </component>
    <component name="Microsoft-Windows-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <DiskConfiguration>
        <Disk wcm:action="add">
          <DiskID>0</DiskID><WillWipeDisk>true</WillWipeDisk>
          <CreatePartitions>
            <CreatePartition wcm:action="add"><Order>1</Order><Type>EFI</Type><Size>300</Size></CreatePartition>
            <CreatePartition wcm:action="add"><Order>2</Order><Type>MSR</Type><Size>16</Size></CreatePartition>
            <CreatePartition wcm:action="add"><Order>3</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>
          </CreatePartitions>
          <ModifyPartitions>
            <ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Format>FAT32</Format><Label>System</Label></ModifyPartition>
            <ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>3</PartitionID><Format>NTFS</Format><Label>Windows</Label><Letter>C</Letter></ModifyPartition>
          </ModifyPartitions>
        </Disk>
      </DiskConfiguration>
      <ImageInstall><OSImage>
        <InstallFrom><MetaData wcm:action="add"><Key>/IMAGE/NAME</Key><Value>Windows 11 Pro</Value></MetaData></InstallFrom>
        <InstallTo><DiskID>0</DiskID><PartitionID>3</PartitionID></InstallTo>
      </OSImage></ImageInstall>
      <UserData>
        <ProductKey><Key>VK7JG-NPHTM-C97JM-9MPGT-3V66T</Key><WillShowUI>Never</WillShowUI></ProductKey>
        <AcceptEula>true</AcceptEula>
      </UserData>
    </component>
  </settings>
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <ComputerName>SL-WIN11</ComputerName><TimeZone>UTC</TimeZone>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <InputLocale>en-US</InputLocale><SystemLocale>en-US</SystemLocale><UILanguage>en-US</UILanguage><UserLocale>en-US</UserLocale>
    </component>
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <OOBE>
        <HideEULAPage>true</HideEULAPage><HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens><HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <ProtectYourPC>3</ProtectYourPC>
      </OOBE>
      <UserAccounts><LocalAccounts><LocalAccount wcm:action="add">
        <Name>tester</Name><DisplayName>StreamLite Tester</DisplayName><Group>Administrators</Group>
        <Password><Value>$pw</Value><PlainText>true</PlainText></Password>
      </LocalAccount></LocalAccounts></UserAccounts>
      <AutoLogon><Enabled>true</Enabled><Username>tester</Username><LogonCount>9999999</LogonCount>
        <Password><Value>$pw</Value><PlainText>true</PlainText></Password></AutoLogon>
      <FirstLogonCommands>
        <SynchronousCommand wcm:action="add"><Order>1</Order><CommandLine>powercfg /change standby-timeout-ac 0</CommandLine></SynchronousCommand>
        <SynchronousCommand wcm:action="add"><Order>2</Order><CommandLine>powercfg /change monitor-timeout-ac 0</CommandLine></SynchronousCommand>
        <SynchronousCommand wcm:action="add"><Order>3</Order><CommandLine>cmd /c echo ok&gt; C:\lab-setup.status</CommandLine></SynchronousCommand>
        <SynchronousCommand wcm:action="add"><Order>4</Order><CommandLine>shutdown /s /t 60</CommandLine></SynchronousCommand>
      </FirstLogonCommands>
    </component>
  </settings>
</unattend>
"@
    Remove-Item "$dir\answer.iso" -ErrorAction SilentlyContinue
    Invoke-Tools "cd /lab/seeds/$name && xorriso -as mkisofs -quiet -o answer.iso -V ANSWER -J autounattend.xml"
    $vdir = "$lab\vms\$name"; New-Item -ItemType Directory -Force $vdir | Out-Null
    $v = New-VM -Name $name -Generation 2 -MemoryStartupBytes 6GB -NewVHDPath "$vdir\$name.vhdx" -NewVHDSizeBytes 100GB -Path "$lab\vms" -SwitchName "Default Switch"
    Set-VM -VM $v -ProcessorCount 6 -DynamicMemory -MemoryMinimumBytes 2GB -MemoryMaximumBytes 8GB -AutomaticCheckpointsEnabled $false `
        -CheckpointType Standard -AutomaticStopAction ShutDown -Notes "StreamLite lab (Windows 11, $iso)"
    Set-VMKeyProtector -VM $v -NewLocalKeyProtector
    Enable-VMTPM -VM $v
    Set-VMVideo -VM $v -HorizontalResolution 1920 -VerticalResolution 1080 -ResolutionType Single
    $dvd = Add-VMDvdDrive -VM $v -Path "$lab\iso\$iso" -Passthru
    Add-VMDvdDrive -VM $v -Path "$dir\answer.iso"
    Set-VMFirmware -VM $v -FirstBootDevice $dvd
    Enable-VMIntegrationService -VM $v -Name "Guest Service Interface"
    Start-VM -VM $v
    # The DVD asks "Press any key to boot from CD or DVD" for a few seconds: answer it.
    $kb = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_Keyboard | Where-Object { $_.SystemName -eq $v.Id.Guid.ToUpper() -or $_.SystemName -eq $v.Id.Guid }
    for ($i = 0; $i -lt 12; $i++) { Start-Sleep -Milliseconds 700; if ($kb) { Invoke-CimMethod -InputObject $kb -MethodName TypeKey -Arguments @{ keyCode = 13 } | Out-Null } }
    "$name installing Windows 11..."
}

function Build-BazziteVm([string]$name)
{
    $src = "$lab\bazzite\bazzite.vhdx"
    if (-not (Test-Path $src)) { throw "Bazzite disk missing: run bazzite-build.sh in WSL as root first" }
    $dir = "$lab\vms\$name"; New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item $src "$dir\$name.vhdx"
    $v = New-VM -Name $name -Generation 2 -MemoryStartupBytes 6GB -VHDPath "$dir\$name.vhdx" -Path "$lab\vms" -SwitchName "Default Switch"
    Set-VM -VM $v -ProcessorCount 6 -DynamicMemory -MemoryMinimumBytes 2GB -MemoryMaximumBytes 8GB -AutomaticCheckpointsEnabled $false `
        -CheckpointType Standard -AutomaticStopAction ShutDown -Notes "StreamLite lab (Bazzite, bootc image)"
    Set-VMFirmware -VM $v -EnableSecureBoot Off
    Set-VMVideo -VM $v -HorizontalResolution 1920 -VerticalResolution 1080 -ResolutionType Single
    Start-VM -VM $v
    "$name started: switch on SSH from its console once (key/type), run setup.sh kde over SSH, reboot, then .\vmlab.ps1 checkpoint $name (README)"
}

function Build-Vm([string]$name)
{
    $image, $desk = $Vms[$name]
    if (Get-VM -Name $name -ErrorAction SilentlyContinue) { "$name already exists (remove it first to rebuild)"; return }
    New-LabKeys
    if ($desk -eq "windows") { Build-WindowsVm $name $image; return }
    if ($desk -eq "bazzite") { Build-BazziteVm $name; return }
    $dir = "$lab\vms\$name"; New-Item -ItemType Directory -Force $dir | Out-Null
    $vhd = "$dir\$name.vhdx"
    if (-not (Test-Path $vhd)) {
        Invoke-Tools "qemu-img convert -O vhdx -o subformat=dynamic /lab/images/$image.qcow2 /lab/vms/$name/$name.vhdx.part && mv /lab/vms/$name/$name.vhdx.part /lab/vms/$name/$name.vhdx"
    }
    Resize-VHD -Path $vhd -SizeBytes 80GB
    $seed = New-Seed $name $desk
    $v = New-VM -Name $name -Generation 2 -MemoryStartupBytes 4GB -VHDPath $vhd -Path "$lab\vms" -SwitchName "Default Switch"
    Set-VM -VM $v -ProcessorCount 6 -DynamicMemory -MemoryMinimumBytes 2GB -MemoryMaximumBytes 8GB -AutomaticCheckpointsEnabled $false `
        -CheckpointType Standard -AutomaticStopAction ShutDown -Notes "StreamLite lab ($image, $desk)"
    # Distro kernels and DKMS modules (v4l2loopback) aren't signed for Hyper-V's Microsoft-only Secure Boot.
    Set-VMFirmware -VM $v -EnableSecureBoot Off
    # The desktops take the host's preferred mode (1024x768 unless set here); kernel video= only sizes the console.
    Set-VMVideo -VM $v -HorizontalResolution 1920 -VerticalResolution 1080 -ResolutionType Single
    Add-VMDvdDrive -VM $v -Path $seed
    Set-VMFirmware -VM $v -FirstBootDevice (Get-VMHardDiskDrive -VM $v)
    Enable-VMIntegrationService -VM $v -Name "Guest Service Interface"
    Start-VM -VM $v
    "$name building ($desk on $image)..."
}

function Save-Clean([string]$name)
{
    Stop-VM -Name $name
    Get-VMSnapshot -VMName $name -Name clean -ErrorAction SilentlyContinue | Remove-VMSnapshot
    Checkpoint-VM -Name $name -SnapshotName clean
}

function Step-Vm([string]$name, [string]$phase)
{
    # One step of a build, so several VMs build side by side. Phases: setup (first-boot setup running; the VM powers
    # itself off at the end) -> booting (started again without the seed) -> again (setup re-run) -> done | failed.
    $ErrorActionPreference = "Continue"   # ssh's messages on stderr aren't failures
    $v = Get-VM -Name $name
    if ($phase -eq "setup") {
        if ($v.State -ne "Off") { return "setup" }
        Get-VMDvdDrive -VM $v | Remove-VMDvdDrive
        Start-VM -VM $v
        return "booting"
    }
    if ($v.State -ne "Running") { Start-VM -VM $v; return $phase }
    if (Is-Windows $name) {
        try { $status = Invoke-Command -VMName $name -Credential (Get-Cred) { (Get-Content C:\lab-setup.status -ErrorAction SilentlyContinue) -join "" } -ErrorAction Stop }
        catch { return $phase }
        if ($status -notmatch '^ok') { "$name FAILED: setup said '$status'"; return "failed" }
        Save-Clean $name; "$name ready"; return "done"
    }
    $ip = Get-Ip $name 0
    if (-not $ip) { return $phase }
    $status = & ssh.exe @(Ssh-Args $ip) "cat /var/log/lab-setup.status; pgrep -f lab-setup.sh >/dev/null && echo busy" 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $status) { return $phase }
    $first = @($status)[0]
    if ($first -match '^ok') { Save-Clean $name; "$name ready: $first"; return "done" }
    if ($status -contains "busy") { return "again" }
    if ($phase -eq "again") { & ssh.exe @(Ssh-Args $ip) "tail -n 25 /var/log/lab-setup.log"; "$name FAILED: $first"; return "failed" }
    # Cut short (the PC restarted, say): the setup is safe to run again, from where the packages left off.
    "${name}: setup didn't finish ($first) - running it again"
    $tmp = "$lab\seeds\$name\lab-setup.sh"; Write-Lf $tmp (Get-Content "$here\setup.sh" -Raw)
    & scp.exe -q @((Ssh-Args $ip) | Select-Object -SkipLast 1) $tmp "tester@${ip}:lab-setup.sh"
    & ssh.exe @(Ssh-Args $ip) "sudo install -m 755 lab-setup.sh /root/lab-setup.sh && rm lab-setup.sh && sudo systemd-run --unit=lab-setup-again-`$(date +%s) bash -c 'bash /root/lab-setup.sh $($Vms[$name][1]) >> /var/log/lab-setup.log 2>&1'" | Out-Null
    "again"
}

function Get-Keyboard([string]$name)
{
    $id = (Get-VM -Name $name).Id.Guid
    Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_Keyboard | Where-Object { $_.SystemName -eq $id -or $_.SystemName -eq $id.ToUpper() }
}

function Send-Keys([string]$name, [string]$combo)
{
    # "ctrl+alt+f3", "enter", "tab": held modifiers, then the key (Windows virtual-key codes).
    $codes = @{ ctrl = 0x11; alt = 0x12; shift = 0x10; win = 0x5B; enter = 0x0D; tab = 0x09; esc = 0x1B; space = 0x20; backspace = 0x08
                up = 0x26; down = 0x28; left = 0x25; right = 0x27 }
    1..12 | ForEach-Object { $codes["f$_"] = 0x6F + $_ }
    $kb = Get-Keyboard $name
    $parts = $combo.ToLower() -split '\+'
    $keys = foreach ($p in $parts) { if ($codes.Contains($p)) { $codes[$p] } elseif ($p.Length -eq 1) { [int][char]$p.ToUpper() } else { throw "Unknown key '$p'" } }
    foreach ($k in $keys[0..($keys.Count - 2)]) { if ($keys.Count -gt 1) { Invoke-CimMethod -InputObject $kb -MethodName PressKey -Arguments @{ keyCode = $k } | Out-Null } }
    Invoke-CimMethod -InputObject $kb -MethodName TypeKey -Arguments @{ keyCode = $keys[-1] } | Out-Null
    if ($keys.Count -gt 1) { foreach ($k in $keys[0..($keys.Count - 2)]) { Invoke-CimMethod -InputObject $kb -MethodName ReleaseKey -Arguments @{ keyCode = $k } | Out-Null } }
}

function Send-Text([string]$name, [string]$text)
{
    # Hyper-V's TypeText sends Alt+numpad codes, which only Windows understands: Linux gets it key by key (US layout).
    $kb = Get-Keyboard $name
    if (Is-Windows $name) { Invoke-CimMethod -InputObject $kb -MethodName TypeText -Arguments @{ asciiText = $text } | Out-Null; return }
    $oem = @{ " " = 0x20; "-" = 0xBD; "=" = 0xBB; "[" = 0xDB; "]" = 0xDD; "\" = 0xDC; ";" = 0xBA; "'" = 0xDE; "," = 0xBC; "." = 0xBE; "/" = 0xBF; "``" = 0xC0 }
    $shifted = @{ "!" = "1"; "@" = "2"; "#" = "3"; "$" = "4"; "%" = "5"; "^" = "6"; "&" = "7"; "*" = "8"; "(" = "9"; ")" = "0"
                  "_" = "-"; "+" = "="; "{" = "["; "}" = "]"; "|" = "\"; ":" = ";"; '"' = "'"; "<" = ","; ">" = "."; "?" = "/"; "~" = "``" }
    foreach ($c in $text.ToCharArray()) {
        $s = [string]$c; $shift = $c -cmatch '[A-Z]'
        if ($shifted.ContainsKey($s)) { $s = $shifted[$s]; $shift = $true }
        $k = if ($oem.ContainsKey($s)) { $oem[$s] } elseif ($s -match '^[a-z0-9]$') { [int][char]$s.ToUpper() } else { throw "Can't type '$c'" }
        if ($shift) { Invoke-CimMethod -InputObject $kb -MethodName PressKey -Arguments @{ keyCode = 0x10 } | Out-Null }
        Invoke-CimMethod -InputObject $kb -MethodName TypeKey -Arguments @{ keyCode = $k } | Out-Null
        if ($shift) { Invoke-CimMethod -InputObject $kb -MethodName ReleaseKey -Arguments @{ keyCode = 0x10 } | Out-Null }
    }
}

function Save-Shot([string]$name, [string]$out)
{
    $ns = "root\virtualization\v2"
    $cs = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem -Filter "ElementName='$name'"
    $vssd = Get-CimAssociatedInstance -InputObject $cs -ResultClassName Msvm_VirtualSystemSettingData | Where-Object VirtualSystemType -eq "Microsoft:Hyper-V:System:Realized"
    $head = Get-CimAssociatedInstance -InputObject $cs -ResultClassName Msvm_VideoHead | Select-Object -First 1
    $w = [int]$head.CurrentHorizontalResolution; $h = [int]$head.CurrentVerticalResolution
    $svc = Get-CimInstance -Namespace $ns -ClassName Msvm_VirtualSystemManagementService
    $r = Invoke-CimMethod -InputObject $svc -MethodName GetVirtualSystemThumbnailImage -Arguments @{ TargetSystem = $vssd; WidthPixels = [uint16]$w; HeightPixels = [uint16]$h }
    if (-not $r.ImageData) { throw "Hyper-V gave no picture of $name (is it running?)" }
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format16bppRgb565)
    $bits = $bmp.LockBits((New-Object Drawing.Rectangle 0, 0, $w, $h), [Drawing.Imaging.ImageLockMode]::WriteOnly, $bmp.PixelFormat)
    for ($y = 0; $y -lt $h; $y++) { [Runtime.InteropServices.Marshal]::Copy([byte[]]$r.ImageData, $y * $w * 2, [IntPtr]($bits.Scan0.ToInt64() + $y * $bits.Stride), $w * 2) }
    $bmp.UnlockBits($bits)
    $bmp.Save($out, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "$out (${w}x$h)"
}

switch ($What)
{
    "list" {
        foreach ($n in $Vms.Keys) {
            $v = Get-VM -Name $n -ErrorAction SilentlyContinue
            if (-not $v) { "{0,-22} not built" -f $n; continue }
            $clean = if (Get-VMSnapshot -VM $v -Name clean -ErrorAction SilentlyContinue) { "clean checkpoint" } else { "no checkpoint" }
            "{0,-22} {1,-8} {2,-16} {3}" -f $n, $v.State, (Get-Ip $n 0), $clean
        }
    }
    "build" {
        $names = if ($Vm -eq "all") { @($Vms.Keys) } else { @($Vm -split ",") }
        foreach ($n in $names) { if (-not $Vms.Contains($n)) { throw "Unknown VM '$n': $($Vms.Keys -join ', ')" } }
        foreach ($n in $names) { Build-Vm $n }
        # Each VM powers itself off when its setup is done (up to an hour or so for a full desktop download).
        $pending = [Collections.ArrayList]@($names | Where-Object { $Vms[$_][1] -ne "bazzite" -and -not (Get-VMSnapshot -VMName $_ -Name clean -ErrorAction SilentlyContinue) })
        # A VM already running (a build cut short) is checked over SSH; one that's off may not have run its setup yet.
        $phase = @{}
        foreach ($n in $pending) { $phase[$n] = if ((Get-VM -Name $n).State -eq "Running" -and -not (Get-VMDvdDrive -VMName $n)) { "booting" } else { "setup" } }
        $until = (Get-Date).AddMinutes(240)
        while ($pending.Count -and (Get-Date) -lt $until) {
            foreach ($n in @($pending)) {
                try { $out = @(Step-Vm $n $phase[$n]) } catch { $out = @("$n FAILED: $_", "failed") }
                $phase[$n] = $out[-1]
                if ($out.Count -gt 1) { $out[0..($out.Count - 2)] }
                if ($phase[$n] -in "done", "failed") { $pending.Remove($n) }
            }
            Start-Sleep -Seconds 30
        }
        foreach ($n in $pending) { "$n still building after 4 hours ($($phase[$n]))" }
    }
    "start" { Need-Vm; Start-VM -Name $Vm; if (Wait-Up $Vm 300) { "$Vm up at $(Get-Ip $Vm 0)" } else { "$Vm started, not reachable yet" } }
    "stop" { Need-Vm; Stop-VM -Name $Vm -Force:$($Rest -contains "-force"); "$Vm off" }
    "reset" {
        Need-Vm
        Stop-VM -Name $Vm -TurnOff -Force -ErrorAction SilentlyContinue
        Restore-VMSnapshot -VMName $Vm -Name clean -Confirm:$false
        Start-VM -Name $Vm
        if (Wait-Up $Vm 300) { "$Vm back to clean, up at $(Get-Ip $Vm 0)" } else { "$Vm restored; not reachable yet" }
    }
    "run" {
        Need-Vm
        if (Is-Windows $Vm) { Invoke-Command -VMName $Vm -Credential (Get-Cred) -ScriptBlock ([scriptblock]::Create($Rest -join " ")); return }
        Invoke-Vm $Vm ($Rest -join " "); exit $LASTEXITCODE
    }
    "put" {
        Need-Vm
        $dest = if ($Rest.Count -gt 1) { $Rest[1] } else { "" }
        if (Is-Windows $Vm) {
            $s = New-PSSession -VMName $Vm -Credential (Get-Cred)
            Copy-Item -ToSession $s -Path $Rest[0] -Destination $(if ($dest) { $dest } else { "C:\Users\tester\" + (Split-Path $Rest[0] -Leaf) }); Remove-PSSession $s; return
        }
        $ip = Wait-Ssh $Vm 180; if (-not $ip) { throw "$Vm doesn't answer SSH (is it running?)" }
        & scp.exe -q @((Ssh-Args $ip) | Select-Object -SkipLast 1) $Rest[0] "tester@${ip}:$dest"
    }
    "get" {
        Need-Vm
        $dest = if ($Rest.Count -gt 1) { $Rest[1] } else { "." }
        if (Is-Windows $Vm) { $s = New-PSSession -VMName $Vm -Credential (Get-Cred); Copy-Item -FromSession $s -Path $Rest[0] -Destination $dest; Remove-PSSession $s; return }
        $ip = Wait-Ssh $Vm 180; if (-not $ip) { throw "$Vm doesn't answer SSH (is it running?)" }
        & scp.exe -q @((Ssh-Args $ip) | Select-Object -SkipLast 1) "tester@${ip}:$($Rest[0])" $dest
    }
    "shot" { Need-Vm; Save-Shot $Vm $(if ($Rest) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Rest[0]) } else { "$PWD\$Vm.png" }) }
    "usb" {
        Need-Vm
        $action, $busid = $Rest
        $usbipd = "$env:ProgramFiles\usbipd-win\usbipd.exe"
        $line = & $usbipd list | Where-Object { $_ -match "^$([regex]::Escape($busid))\s" }
        if (-not $line) { throw "No USB device at $busid (usbipd list)" }
        # The PC's speakers are loud: never hand them to a VM, where a test could play through them.
        if ($line -match "Realtek USB Audio") { throw "Refusing: $busid is the speakers (Realtek USB Audio)" }
        $hostIp = (Get-NetIPAddress -InterfaceAlias "vEthernet (Default Switch)" -AddressFamily IPv4).IPAddress
        $usbip = 'u=$(command -v usbip || ls /usr/lib/linux-tools/*/usbip 2>/dev/null | tail -n1); sudo modprobe vhci-hcd;'
        if (Is-Windows $Vm) { throw "USB passthrough is for the Linux VMs" }
        if ($action -eq "attach") {
            & $usbipd bind --busid $busid | Out-Null
            Invoke-Vm $Vm "$usbip sudo `$u attach -r $hostIp -b $busid && sleep 2 && sudo `$u port"
        } else {
            Invoke-Vm $Vm "$usbip for p in `$(sudo `$u port | sed -n 's/^Port \([0-9]*\):.*/\1/p'); do sudo `$u detach -p `$p; done" | Out-Null
            Start-Sleep -Seconds 1
            $ErrorActionPreference = "Continue"   # usbipd says "already not attached" on stderr
            & $usbipd detach --busid $busid 2>&1 | Out-Null
            & $usbipd unbind --busid $busid 2>&1 | Out-Null
            if (-not (& $usbipd list | Where-Object { $_ -match "^$([regex]::Escape($busid))\s.*Not shared" })) { throw "$busid is still shared (usbipd list)" }
            "$busid is back with Windows"
        }
    }
    "checkpoint" {
        Need-Vm
        Stop-VM -Name $Vm
        Get-VMSnapshot -VMName $Vm -Name clean -ErrorAction SilentlyContinue | Remove-VMSnapshot
        Checkpoint-VM -Name $Vm -SnapshotName clean
        "$Vm saved as clean"
    }
    "type" { Need-Vm; Send-Text $Vm ($Rest -join " "); "typed" }
    "key" { Need-Vm; foreach ($k in $Rest) { Send-Keys $Vm $k; Start-Sleep -Milliseconds 300 }; "pressed $($Rest -join ' ')" }
    "remove" {
        Need-Vm
        Stop-VM -Name $Vm -TurnOff -Force -ErrorAction SilentlyContinue
        Remove-VM -Name $Vm -Force
        Remove-Item -Recurse -Force "$lab\vms\$Vm", "$lab\seeds\$Vm" -ErrorAction SilentlyContinue
        "$Vm removed"
    }
    default { throw "Unknown command '$What'" }
}
