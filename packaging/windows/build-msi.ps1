# Builds the Windows release: dist\StreamLite-<version>-x64.msi and dist\StreamLite-<version>-win-x64-portable.zip.
# Needs the .NET SDK (for the WiX tool: dotnet tool install --global wix --version 5.0.2). The app itself is built
# with the C# compiler that ships with Windows (build.ps1).
param([string]$Version = '')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path (Split-Path $here)
& "$root\build.ps1" | Out-Null
if (-not $Version) { $Version = (Get-Item "$root\bin\StreamLite.exe").VersionInfo.FileVersion -replace '^(\d+\.\d+\.\d+).*', '$1' }

# Stage exactly what gets installed.
$payload = Join-Path $here 'payload'
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Force "$payload\ai", "$payload\cam", "$payload\hrtf" | Out-Null
Copy-Item "$root\bin\StreamLite.exe", "$root\app.ico" $payload
Copy-Item "$root\LICENSE" "$payload\LICENSE.txt"
Copy-Item "$root\THIRD-PARTY-NOTICES.md" "$payload\THIRD-PARTY-NOTICES.txt"
Copy-Item "$root\native\StreamLiteCam.dll" "$payload\cam\"
Get-ChildItem "$root\ai" -File | Copy-Item -Destination "$payload\ai\"
# Every head for 3D audio at its full measurement (SADIE II, tools/make_hrtf.py).
Get-ChildItem "$root\hrtf" -Filter *.blh | Copy-Item -Destination "$payload\hrtf\"
# How this copy was installed, for the updater (the portable zip gets "zip" below).
'msi' | Set-Content "$payload\install-kind.txt" -Encoding ASCII

# License page for the installer (RTF): the GPL text plus the additional permission.
function Rtf([string]$text) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $text.ToCharArray()) {
        $c = [int]$ch
        if ($ch -eq '\' -or $ch -eq '{' -or $ch -eq '}') { [void]$sb.Append('\' + $ch) }
        elseif ($ch -eq "`n") { [void]$sb.Append("\par`n") }
        elseif ($ch -eq "`r") { }
        elseif ($c -gt 127) { [void]$sb.Append('\u' + $c + '?') }
        else { [void]$sb.Append($ch) }
    }
    $sb.ToString()
}
$intro = "StreamLite $Version - free software under the GNU General Public License v3 or later.`n`nAdditional permission (GPLv3 section 7): the copyright holders of StreamLite give you permission to combine StreamLite with ONNX Runtime, Microsoft DirectML and the components of Microsoft Windows, and to distribute the combination, without the terms of the GPL applying to those components.`n`nCredits and the licenses of every component StreamLite uses are in THIRD-PARTY-NOTICES.txt next to StreamLite.exe.`n`n"
$body = Rtf ($intro + (Get-Content "$root\LICENSE" -Raw))
Set-Content "$here\License.rtf" ("{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\fs18 " + $body + "}") -Encoding ASCII

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$msi = Join-Path $dist "StreamLite-$Version-x64.msi"
$wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
if (-not (Test-Path $wix)) { $wix = 'wix' }
Push-Location $here
try {
    & $wix extension add -g WixToolset.UI.wixext/5.0.2 | Out-Null
    & $wix extension add -g WixToolset.Util.wixext/5.0.2 | Out-Null
    & $wix build StreamLite.wxs -arch x64 -d "Version=$Version" -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext -o $msi
    if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }
}
finally { Pop-Location }

# Portable zip: unzip anywhere and run StreamLite.exe.
$zip = Join-Path $dist "StreamLite-$Version-win-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
'zip' | Set-Content "$payload\install-kind.txt" -Encoding ASCII
Compress-Archive -Path "$payload\*" -DestinationPath $zip -CompressionLevel Optimal
'msi' | Set-Content "$payload\install-kind.txt" -Encoding ASCII
Get-Item $msi, $zip | ForEach-Object { '{0}  {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }
