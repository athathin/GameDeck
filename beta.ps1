# Builds GameDeck Beta from the code in this folder: the same program with its own name, data folder and "one at a time"
# lock (see GameDeck\AppInfo.cs), so it runs next to the normal GameDeck. It never updates itself and is not pushed anywhere.
#
#   .\beta.ps1              build it (and set it up the first time)
#   .\beta.ps1 -SyncData    also copy the normal GameDeck's library, settings and covers over the Beta's own
#
# Output: %USERPROFILE%\GameDeck-Beta\GameDeckBeta.exe (and a "GameDeck Beta" shortcut on the desktop).
# This script only builds: it never starts the app.
param([switch]$SyncData)
$ErrorActionPreference = 'Stop'

$out = Join-Path $env:USERPROFILE 'GameDeck-Beta'
$project = Join-Path $PSScriptRoot 'GameDeck\GameDeck.csproj'

if (Get-Process -Name GameDeckBeta -ErrorAction SilentlyContinue) {
    throw 'GameDeck Beta is running: close it first (its files are in use).'
}

dotnet build $project -c Release -p:GameDeckBeta=true -v q -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

# the Beta keeps its own data, so a half-finished feature can't damage the real library; it starts as a copy of the real one
$mainData = Join-Path $env:APPDATA 'GameDeck'
$betaData = Join-Path $env:APPDATA 'GameDeck Beta'
if ((-not (Test-Path $betaData) -or $SyncData) -and (Test-Path $mainData)) {
    New-Item -ItemType Directory -Force $betaData | Out-Null
    foreach ($file in 'library.json', 'userdata.json', 'sessions.json', 'settings.json') {
        $source = Join-Path $mainData $file
        if (Test-Path $source) { Copy-Item $source (Join-Path $betaData $file) -Force }
    }
    # the Beta must open its window (not sit in the tray) and must not fight the real GameDeck over the shortcut / Home button
    $settingsFile = Join-Path $betaData 'settings.json'
    if (Test-Path $settingsFile) {
        $settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
        foreach ($name in 'StartMinimized', 'QuickLaunchEnabled', 'ControllerWake', 'AutoUpdate') {
            if ($settings.PSObject.Properties[$name]) { $settings.$name = $false }
        }
        $settings | ConvertTo-Json | Set-Content $settingsFile -Encoding UTF8
    }
    foreach ($folder in 'covers', 'custom-covers') {
        $source = Join-Path $mainData $folder
        if (Test-Path $source) { robocopy $source (Join-Path $betaData $folder) /E /NFL /NDL /NJH /NJS /NP | Out-Null }
    }
    Write-Host "Copied the normal GameDeck's data to $betaData"
}

# a shortcut on the desktop, once
$shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'GameDeck Beta.lnk'
if (-not (Test-Path $shortcut)) {
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    $link.TargetPath = Join-Path $out 'GameDeckBeta.exe'
    $link.WorkingDirectory = $out
    $link.Save()
}

Write-Host "GameDeck Beta is ready: $out\GameDeckBeta.exe"
