param(
    [string]$GameDir = "",
    [switch]$RestoreBepInEx6
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"
$GameDir = Resolve-GameDir $GameDir
if (Get-Process nslt -ErrorAction SilentlyContinue) { throw "Close the game first." }

$ggm = "$GameDir\nslt_Data\globalgamemanagers"
if (Test-Path "$ggm.nsltvr-backup") { Copy-Item "$ggm.nsltvr-backup" $ggm -Force; Remove-Item "$ggm.nsltvr-backup"; Write-Host "Restored globalgamemanagers." }
Remove-Item "$GameDir\nslt_Data\Plugins\openvr_api.dll" -ErrorAction SilentlyContinue
$manifest = "$GameDir\BepInEx\plugins\NSLTVR\nsltvr.vrmanifest"
# SteamVR library entry "New Super Lucky's Tale VR" (only removable while SteamVR is running)
if ((Test-Path $manifest) -and (Get-Process vrserver -ErrorAction SilentlyContinue) -and (Test-Path "$PSScriptRoot\build\VrUnregister.exe")) {
    & "$PSScriptRoot\build\VrUnregister.exe" $manifest
} elseif (Test-Path $manifest) {
    Write-Host "Note: start SteamVR and run this again to remove 'New Super Lucky's Tale VR' from SteamVR's library (harmless if left)."
}
Remove-Item "$GameDir\steam_appid.txt" -ErrorAction SilentlyContinue   # created so SteamVR can start the game directly
Remove-Item "$GameDir\BepInEx\plugins\NSLTVR" -Recurse -ErrorAction SilentlyContinue
Remove-Item "$GameDir\NSLTVR-launcher" -Recurse -ErrorAction SilentlyContinue
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    Remove-Item (Join-Path $dir "New Super Lucky's Tale VR.lnk") -ErrorAction SilentlyContinue
}
Write-Host "Removed NSLT-VR."

if ($RestoreBepInEx6 -and (Test-Path "$GameDir\_BepInEx6_backup\BepInEx")) {
    Remove-Item "$GameDir\BepInEx" -Recurse -Force
    Remove-Item "$GameDir\winhttp.dll", "$GameDir\doorstop_config.ini", "$GameDir\.doorstop_version" -ErrorAction SilentlyContinue
    Move-Item "$GameDir\_BepInEx6_backup\*" $GameDir -Force
    Remove-Item "$GameDir\_BepInEx6_backup" -Recurse
    Write-Host "Restored the previous BepInEx 6 install."
}
