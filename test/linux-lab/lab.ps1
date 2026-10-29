# StreamLite's Linux test lab (Docker): .\lab.ps1 up | run <command> | shot <file.png> | down
#   up    builds the image and starts the lab; its desktop is at http://127.0.0.1:6080/vnc.html (this PC only)
#   run   runs a command in the lab as the test user, with the desktop and sound set up
#   shot  saves a picture of the lab's desktop
# The repository is in the lab at /src (read only); linux/publish at /opt/streamlite-dev.
param([Parameter(Position = 0)][string]$What = "up", [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Rest)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$root = (Resolve-Path "$here\..\..").Path
$name = "streamlite-lab"
switch ($What)
{
    "up" {
        docker build -q -t streamlite-lab:1 $here | Out-Null
        if (docker ps -a --format "{{.Names}}" | Where-Object { $_ -eq $name }) { docker rm -f $name | Out-Null }
        docker run -d --name $name --shm-size 512m -p 127.0.0.1:6080:6080 `
            -v "${root}:/src:ro" -v "${root}\linux\publish:/opt/streamlite-dev:ro" streamlite-lab:1 | Out-Null
        for ($i = 0; $i -lt 60; $i++) { if ((docker logs $name 2>&1) -match "lab ready") { break }; Start-Sleep -Milliseconds 500 }
        docker logs $name 2>&1 | Select-Object -Last 3
        "desktop: http://127.0.0.1:6080/vnc.html?autoconnect=1&resize=scale"
    }
    "run" { $OutputEncoding = New-Object System.Text.UTF8Encoding $false; (". /tmp/lab-env; " + ($Rest -join " ")) | docker exec -i $name bash -l }   # through stdin: no quoting trouble
    "shot" {
        $out = if ($Rest) { $Rest[0] } else { "lab.png" }
        docker exec $name bash -lc ". /tmp/lab-env; import -window root /tmp/shot.png" | Out-Null
        docker cp "${name}:/tmp/shot.png" $out | Out-Null
        $out
    }
    "down" { docker rm -f $name | Out-Null; "stopped" }
}
