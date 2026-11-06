param(
    [string]$GameDir = "",
    [string]$OpenVRApi = ""
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$deps = Join-Path $root "deps"
. "$root\common.ps1"
$GameDir = Resolve-GameDir $GameDir
if (-not (Test-Path "$deps\BepInEx\core\BepInEx.dll") -or -not (Test-Path "$deps\openvr\openvr_api.dll")) { & "$root\fetch-deps.ps1" }
if (-not $OpenVRApi) { $OpenVRApi = "$deps\openvr\openvr_api.dll" }
Write-Host "Game: $GameDir"

if (Get-Process nslt -ErrorAction SilentlyContinue) { throw "New Super Lucky's Tale is running. Close it first." }
if (-not (Test-Path "$GameDir\nslt.exe")) { throw "Game not found at $GameDir" }
if (-not (Test-Path $OpenVRApi)) { throw "openvr_api.dll not found at $OpenVRApi (pass -OpenVRApi <path>)" }
if (-not (Test-Path "$root\build\NSLTVR.dll")) { throw "Plugin not built. Run build.ps1 first." }

# 1. Replace BepInEx 6 (crashes on this game's old Mono) with BepInEx 5, keeping a backup.
if (Test-Path "$GameDir\BepInEx\core\BepInEx.Unity.Mono.dll") {
    $bk = "$GameDir\_BepInEx6_backup"
    New-Item -ItemType Directory -Force $bk | Out-Null
    Move-Item "$GameDir\BepInEx" "$bk\BepInEx"
    foreach ($f in "doorstop_config.ini", "winhttp.dll", ".doorstop_version") {
        if (Test-Path "$GameDir\$f") { Move-Item "$GameDir\$f" "$bk\$f" -Force }
    }
    Write-Host "Backed up BepInEx 6 to $bk"
}
Copy-Item "$deps\winhttp.dll", "$deps\doorstop_config.ini", "$deps\.doorstop_version" $GameDir -Force
New-Item -ItemType Directory -Force "$GameDir\BepInEx\core", "$GameDir\BepInEx\plugins\NSLTVR" | Out-Null
Copy-Item "$deps\BepInEx\core\*" "$GameDir\BepInEx\core" -Force
Write-Host "BepInEx 5 installed."

# 2. Plugin
Copy-Item "$root\build\NSLTVR.dll" "$GameDir\BepInEx\plugins\NSLTVR\NSLTVR.dll" -Force
# SteamVR Input: action manifest + default controller layouts (grips/trackpads unbound)
New-Item -ItemType Directory -Force "$GameDir\BepInEx\plugins\NSLTVR\input" | Out-Null
Copy-Item "$root\input\*.json" "$GameDir\BepInEx\plugins\NSLTVR\input\" -Force
# VR shortcut: desktop + Start menu "New Super Lucky's Tale VR" -> launcher (starts SteamVR if needed, then has
# SteamVR launch the VR entry). Lives outside the plugins folder so BepInEx doesn't try to load its openvr_api.dll.
New-Item -ItemType Directory -Force "$GameDir\NSLTVR-launcher" | Out-Null
Copy-Item "$root\build\nsltvr-launch.exe" "$GameDir\NSLTVR-launcher\" -Force
Copy-Item $OpenVRApi "$GameDir\NSLTVR-launcher\openvr_api.dll" -Force
$ws = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $lnk = $ws.CreateShortcut((Join-Path $dir "New Super Lucky's Tale VR.lnk"))
    $lnk.TargetPath = "$GameDir\NSLTVR-launcher\nsltvr-launch.exe"
    $lnk.WorkingDirectory = "$GameDir\NSLTVR-launcher"
    $lnk.IconLocation = "$GameDir\nslt.exe,0"
    $lnk.Description = "New Super Lucky's Tale in VR (NSLT-VR mod)"
    $lnk.Save()
}
Write-Host "Play in VR: the 'New Super Lucky's Tale VR' shortcut (desktop / Start menu), or that entry in SteamVR's library. Steam's normal entry plays flat."
Write-Host "Plugin installed."

# 3. OpenVR runtime loader for Unity's built-in VR
Copy-Item $OpenVRApi "$GameDir\nslt_Data\Plugins\openvr_api.dll" -Force
Write-Host "openvr_api.dll installed."

# 4. Enable OpenVR in the build settings
& "$root\build\GgmPatcher.exe" "$GameDir\nslt_Data\globalgamemanagers" --write
if ($LASTEXITCODE -ne 0) { throw "globalgamemanagers patch failed" }

Write-Host ""
Write-Host "Done. To play in VR, use the 'New Super Lucky's Tale VR' shortcut on your desktop."
Write-Host "Optional: run add-steam-vr-shortcut.ps1 to also add 'New Super Lucky's Tale VR' to your Steam and SteamVR libraries (closes and restarts Steam)."
Write-Host "Steam's normal 'New Super Lucky's Tale' entry plays the flat game."
Write-Host "Log: $GameDir\BepInEx\LogOutput.log   Config (after first run): $GameDir\BepInEx\config\nsltvr.mod.cfg"
