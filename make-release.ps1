# Builds everything and packages a ready-to-install zip: dist\NSLT-VR-v<version>.zip
# (scripts + prebuilt mod/tools + BepInEx + openvr_api.dll, so users only run install.ps1).
param([string]$Version = "")
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Version) { $Version = [regex]::Match((Get-Content "$root\src\Plugin.cs" -Raw), 'Version = "([^"]+)"').Groups[1].Value }

& "$root\build.ps1"
& "$root\fetch-deps.ps1"

$name = "NSLT-VR-v$Version"
$stage = Join-Path $root "dist\$name"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\build", "$stage\input", "$stage\deps\openvr" | Out-Null

Copy-Item "$root\install.ps1", "$root\uninstall.ps1", "$root\add-steam-vr-shortcut.ps1", "$root\common.ps1", "$root\fetch-deps.ps1", "$root\README.md", "$root\LICENSE" $stage
Copy-Item "$root\build\NSLTVR.dll", "$root\build\nsltvr-launch.exe", "$root\build\GgmPatcher.exe", "$root\build\SteamShortcut.exe", "$root\build\VrUnregister.exe" "$stage\build"
Copy-Item "$root\input\*.json" "$stage\input"
Copy-Item "$root\deps\BepInEx" "$stage\deps" -Recurse
Copy-Item "$root\deps\winhttp.dll", "$root\deps\doorstop_config.ini", "$root\deps\.doorstop_version" "$stage\deps" -Force
Copy-Item "$root\deps\openvr\openvr_api.dll" "$stage\deps\openvr"

@"
NSLT-VR v$Version - third-party components included in this download
=======================================================================

BepInEx 5.4.23.6 (deps\BepInEx, deps\winhttp.dll, doorstop files)
  Mod loader. LGPL-2.1. Source and license: https://github.com/BepInEx/BepInEx
  Includes HarmonyX (MIT, https://github.com/BepInEx/HarmonyX), MonoMod (MIT, https://github.com/MonoMod/MonoMod),
  Mono.Cecil (MIT, https://github.com/jbevain/cecil) and Unity Doorstop (LGPL-2.1, https://github.com/NeighTools/UnityDoorstop).

openvr_api.dll 1.14.15 (deps\openvr)
  Valve's OpenVR runtime loader. BSD-3-Clause, Copyright (c) 2015, Valve Corporation.
  Source and license: https://github.com/ValveSoftware/openvr

The NSLT-VR mod itself (build\*.dll, build\*.exe, scripts) is MIT licensed - see LICENSE.
Built from https://github.com/technewsguy/NSLT-VR
New Super Lucky's Tale and Lucky's Tale belong to Playful. This is an unofficial fan mod.
"@ | Set-Content "$stage\THIRD-PARTY-NOTICES.txt" -Encoding ASCII

$zip = Join-Path $root "dist\$name.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path $stage -DestinationPath $zip
Write-Host "Release package: $zip  ($([math]::Round((Get-Item $zip).Length / 1MB, 2)) MB)"
