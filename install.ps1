# Installs StreamLite for the current user (no admin): copies the exe and creates Desktop + Start menu shortcuts.
# Upgrading from the old name (Broadcast Lite) is handled here and by the app itself on its first run.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& "$root\build.ps1" | Out-Null
$dest = Join-Path $env:LOCALAPPDATA 'Programs\StreamLite'
$oldDir = Join-Path $env:LOCALAPPDATA 'Programs\BroadcastLite'
$newTask = 'StreamLite (Administrator)'
$oldTask = 'Broadcast Lite (Administrator)'

function TaskExists($name) { cmd /c "schtasks /Query /TN `"$name`" >nul 2>&1"; return $LASTEXITCODE -eq 0 }
function RunSchtasks($action, $name) { cmd /c "schtasks /$action /TN `"$name`" >nul 2>&1" }

# Stop running copies: ask nicely first (works for an elevated copy too; versions before 2.2.2 just show their window),
# then elevated ones through their task.
$installed = Join-Path $dest 'StreamLite.exe'
if ((Get-Process StreamLite -ErrorAction SilentlyContinue) -and (Test-Path $installed) -and [version](Get-Item $installed).VersionInfo.FileVersion -ge [version]'2.2.2') {
    Start-Process $installed -ArgumentList '--quit' -Wait
}
foreach ($t in $newTask, $oldTask) { if (TaskExists $t) { RunSchtasks End $t } }
Get-Process StreamLite, BroadcastLite -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
for ($i = 0; $i -lt 40 -and (Get-Process StreamLite, BroadcastLite -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
Start-Sleep -Milliseconds 300

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$root\bin\StreamLite.exe" $dest -Force
Copy-Item "$root\app.ico" $dest -Force
# Tells the updater how this copy was installed (it updates it with the portable zip).
'user' | Set-Content (Join-Path $dest 'install-kind.txt') -Encoding ASCII
# StreamLite Camera media source (native; registered by the app itself).
$camDll = Join-Path $root 'native\StreamLiteCam.dll'
if (Test-Path $camDll) {
    New-Item -ItemType Directory -Force (Join-Path $dest 'cam') | Out-Null
    Copy-Item $camDll (Join-Path $dest 'cam\StreamLiteCam.dll') -Force
}
# DeepFilterNet noise removal (ONNX Runtime + DirectML + model): only files that changed.
if (Test-Path "$root\ai") {
    $aiDest = Join-Path $dest 'ai'
    New-Item -ItemType Directory -Force $aiDest | Out-Null
    foreach ($f in Get-ChildItem "$root\ai" -File) {
        $t = Join-Path $aiDest $f.Name
        if (-not (Test-Path $t) -or (Get-Item $t).Length -ne $f.Length -or (Get-Item $t).LastWriteTimeUtc -lt $f.LastWriteTimeUtc) { Copy-Item $f.FullName $t -Force }
    }
}
# Every head for 3D audio at its full measurement (SADIE II): only files that changed.
if (Test-Path "$root\hrtf") {
    $hDest = Join-Path $dest 'hrtf'
    New-Item -ItemType Directory -Force $hDest | Out-Null
    foreach ($f in Get-ChildItem "$root\hrtf" -Filter *.blh) {
        $t = Join-Path $hDest $f.Name
        if (-not (Test-Path $t) -or (Get-Item $t).Length -ne $f.Length -or (Get-Item $t).LastWriteTimeUtc -lt $f.LastWriteTimeUtc) { Copy-Item $f.FullName $t -Force }
    }
}

$shell = New-Object -ComObject WScript.Shell
foreach ($folder in [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs')) {
    $old = Join-Path $folder 'Broadcast Lite.lnk'
    if (Test-Path $old) { Remove-Item $old -Force }
    $s = $shell.CreateShortcut((Join-Path $folder 'StreamLite.lnk'))
    $s.TargetPath = Join-Path $dest 'StreamLite.exe'
    $s.WorkingDirectory = $dest
    $s.IconLocation = (Join-Path $dest 'app.ico') + ',0'
    $s.Description = 'Microphone noise removal, EQ, 3D game audio and camera controls'
    $s.Save()
}

# Task launches start in the tray; the task's arguments are fixed, so the app reads this file instead.
New-Item -ItemType Directory -Force (Join-Path $env:APPDATA 'StreamLite') | Out-Null
$pending = Join-Path $env:APPDATA 'StreamLite\pending-launch.txt'

if (TaskExists $oldTask) {
    # The old admin task points at the old exe: run the new code from there once; it re-creates the task as
    # "StreamLite (Administrator)" for the new folder, removes the old one and restarts from the new folder.
    Copy-Item "$root\bin\StreamLite.exe" (Join-Path $oldDir 'BroadcastLite.exe') -Force
    'minimized' | Set-Content $pending
    RunSchtasks Run $oldTask
    Write-Output "Installed to $dest (migrating the administrator task)"
}
elseif (TaskExists $newTask) {
    'minimized' | Set-Content $pending
    RunSchtasks Run $newTask
    Write-Output "Installed to $dest (started as administrator)"
}
else {
    Start-Process (Join-Path $dest 'StreamLite.exe') -ArgumentList '--minimized'
    if (Test-Path $oldDir) { Remove-Item $oldDir -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Output "Installed to $dest"
}
