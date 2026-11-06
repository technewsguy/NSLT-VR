# Adds "New Super Lucky's Tale VR" to the Steam library as a non-Steam game with "Include in VR Library" on, plus the
# game's Steam artwork (cover, header, background, logo). It then shows in Steam's library and in SteamVR's library,
# launches with -nsltvr (= VR mode in the mod), and Steam treats it as a VR game (no SteamVR "Resume Game" menu).
# Steam has to be closed while its shortcut list is edited; this script closes and restarts it.
param([string]$GameDir = "")
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"
$GameDir = Resolve-GameDir $GameDir
$steam = (Get-ItemProperty HKCU:\Software\Valve\Steam).SteamPath
$steamExe = (Get-ItemProperty HKCU:\Software\Valve\Steam).SteamExe
$user = (Get-ItemProperty HKCU:\Software\Valve\Steam\ActiveProcess).ActiveUser
if (-not $user) { $user = (Get-ChildItem "$steam\userdata" -Directory | Select-Object -First 1).Name }
$cfg = "$steam\userdata\$user\config"
$tool = "$PSScriptRoot\build\SteamShortcut.exe"   # built by build.ps1

if (Get-Process steam -ErrorAction SilentlyContinue) {
    Write-Host "Closing Steam..."
    Start-Process $steamExe -ArgumentList "-shutdown"
    while (Get-Process steam -ErrorAction SilentlyContinue) { Start-Sleep 1 }
}
if ((Test-Path "$cfg\shortcuts.vdf") -and -not (Test-Path "$cfg\shortcuts.vdf.nsltvr-backup")) { Copy-Item "$cfg\shortcuts.vdf" "$cfg\shortcuts.vdf.nsltvr-backup" }
$id = & $tool "$cfg\shortcuts.vdf" "New Super Lucky's Tale VR" "$GameDir\nslt.exe" $GameDir "$GameDir\nslt.exe" "-nsltvr" 1
$art = "$steam\appcache\librarycache\1038300"
New-Item -ItemType Directory -Force "$cfg\grid" | Out-Null
$pairs = @{ "library_header.jpg" = "$id.jpg"; "library_600x900.jpg" = "${id}p.jpg"; "library_hero.jpg" = "${id}_hero.jpg"; "logo.png" = "${id}_logo.png" }
foreach ($k in $pairs.Keys) { if (Test-Path "$art\$k") { Copy-Item "$art\$k" "$cfg\grid\$($pairs[$k])" -Force } }
Write-Host "Added 'New Super Lucky's Tale VR' (shortcut id $id). Starting Steam..."
Start-Process $steamExe
