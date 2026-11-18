# Builds everything and packages the release:
#   dist\NSLT-VR-Setup-v<version>.exe   the installer on its own (what most people download)
#   dist\NSLT-VR-v<version>.zip         the same installer + README, LICENSE and notices (used by the Scoop bucket)
param([string]$Version = "")
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Version) { $Version = [regex]::Match((Get-Content "$root\src\Plugin.cs" -Raw), 'Version = "([^"]+)"').Groups[1].Value }

& "$root\fetch-deps.ps1"
& "$root\build.ps1"

$name = "NSLT-VR-v$Version"
$stage = Join-Path $root "dist\$name"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item "$root\build\NSLT-VR-Setup.exe", "$root\README.md", "$root\LICENSE" $stage

@"
NSLT-VR v$Version - third-party components included in NSLT-VR-Setup.exe
==========================================================================

BepInEx 5.4.23.6 (installed as BepInEx\, winhttp.dll and doorstop files in the game folder)
  Mod loader. LGPL-2.1. Source and license: https://github.com/BepInEx/BepInEx
  Includes HarmonyX (MIT, https://github.com/BepInEx/HarmonyX), MonoMod (MIT, https://github.com/MonoMod/MonoMod),
  Mono.Cecil (MIT, https://github.com/jbevain/cecil) and Unity Doorstop (LGPL-2.1, https://github.com/NeighTools/UnityDoorstop).

openvr_api.dll 1.14.15 (installed as nslt_Data\Plugins\openvr_api.dll)
  Valve's OpenVR runtime loader. BSD-3-Clause, Copyright (c) 2015, Valve Corporation.
  Source and license: https://github.com/ValveSoftware/openvr

The NSLT-VR mod itself (the installer and BepInEx\plugins\NSLTVR) is MIT licensed - see LICENSE.
Built from https://github.com/technewsguy/NSLT-VR
New Super Lucky's Tale and Lucky's Tale belong to Playful. This is an unofficial fan mod.
"@ | Set-Content "$stage\THIRD-PARTY-NOTICES.txt" -Encoding ASCII

$zip = Join-Path $root "dist\$name.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path $stage -DestinationPath $zip
$exe = Join-Path $root "dist\NSLT-VR-Setup-v$Version.exe"
Copy-Item "$root\build\NSLT-VR-Setup.exe" $exe -Force

foreach ($f in $exe, $zip) {
    Write-Host ("Release file: {0}  ({1} MB)  SHA256 {2}" -f $f, [math]::Round((Get-Item $f).Length / 1MB, 2), (Get-FileHash $f).Hash.ToLower())
}
