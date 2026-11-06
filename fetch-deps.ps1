# Downloads the third-party pieces NSLT-VR needs into .\deps (they are not stored in this repository):
#   - BepInEx 5.4.23.6 for 64-bit Windows (mod loader, LGPL-2.1)  https://github.com/BepInEx/BepInEx
#   - openvr_api.dll 1.14.15 for 64-bit Windows (Valve, BSD-3)      https://github.com/ValveSoftware/openvr
# deps\openvr\openvr_api.cs (Valve's C# bindings, same version) is included in the repository.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$deps = Join-Path $PSScriptRoot "deps"
New-Item -ItemType Directory -Force $deps, "$deps\openvr" | Out-Null

if (-not (Test-Path "$deps\BepInEx\core\BepInEx.dll")) {
    $zip = Join-Path $env:TEMP "BepInEx_win_x64_5.4.23.6.zip"
    Write-Host "Downloading BepInEx 5.4.23.6..."
    Invoke-WebRequest "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.6/BepInEx_win_x64_5.4.23.6.zip" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip -DestinationPath $deps -Force
    Remove-Item $zip
}
if (-not (Test-Path "$deps\openvr\openvr_api.dll")) {
    Write-Host "Downloading openvr_api.dll 1.14.15..."
    Invoke-WebRequest "https://raw.githubusercontent.com/ValveSoftware/openvr/v1.14.15/bin/win64/openvr_api.dll" -OutFile "$deps\openvr\openvr_api.dll" -UseBasicParsing
}
Write-Host "Dependencies ready in $deps"
