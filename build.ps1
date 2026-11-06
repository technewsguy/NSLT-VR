param(
    [string]$GameDir = "",
    [string]$BepInExCore = ""
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. "$root\common.ps1"
$GameDir = Resolve-GameDir $GameDir   # compiles against the game's own Managed DLLs
if (-not (Test-Path (Join-Path $root "deps\BepInEx\core\BepInEx.dll"))) { & "$root\fetch-deps.ps1" }
$managed = Join-Path $GameDir "nslt_Data\Managed"
if (-not $BepInExCore) { $BepInExCore = Join-Path $root "deps\BepInEx\core" }
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $root "build"
New-Item -ItemType Directory -Force $out | Out-Null

$refs = @(
    "$managed\mscorlib.dll", "$managed\System.dll", "$managed\System.Core.dll",
    "$managed\UnityEngine.dll", "$managed\UnityEngine.CoreModule.dll", "$managed\UnityEngine.VRModule.dll",
    "$managed\UnityEngine.UIModule.dll", "$managed\UnityEngine.IMGUIModule.dll", "$managed\UnityEngine.InputModule.dll", "$managed\UnityEngine.ImageConversionModule.dll", "$managed\UnityEngine.TextRenderingModule.dll", "$managed\UnityEngine.ScreenCaptureModule.dll", "$managed\UnityEngine.UI.dll",
    "$managed\Assembly-CSharp.dll",
    "$BepInExCore\BepInEx.dll", "$BepInExCore\0Harmony.dll"
)
$refArgs = $refs | ForEach-Object { "/r:$_" }
$src = @(Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName })
$src += Join-Path $root "deps\openvr\openvr_api.cs"   # Valve's official C# bindings (v1.14.15)

& $csc /nologo /noconfig /nostdlib+ /target:library /optimize+ /debug- /warn:4 `
    "/out:$out\NSLTVR.dll" $refArgs $src
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out\NSLTVR.dll"


# "New Super Lucky's Tale VR" desktop shortcut launcher (starts SteamVR if needed, then has SteamVR launch the VR entry)
& $csc /nologo /target:winexe /optimize+ "/out:$out\nsltvr-launch.exe" (Join-Path $root "launcher\NsltVrLaunch.cs") (Join-Path $root "deps\openvr\openvr_api.cs")
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed" }
Write-Host "Built $out\nsltvr-launch.exe"

# Helper tools used by add-steam-vr-shortcut.ps1 and uninstall.ps1
& $csc /nologo /optimize+ "/out:$out\SteamShortcut.exe" (Join-Path $root "tools\SteamShortcut.cs")
if ($LASTEXITCODE -ne 0) { throw "SteamShortcut build failed" }
& $csc /nologo /optimize+ "/out:$out\VrUnregister.exe" (Join-Path $root "tools\VrUnregister.cs") (Join-Path $root "deps\openvr\openvr_api.cs")
if ($LASTEXITCODE -ne 0) { throw "VrUnregister build failed" }
Write-Host "Built helper tools"
& $csc /nologo /optimize+ "/out:$out\GgmPatcher.exe" (Join-Path $root "patcher\GgmPatcher.cs")
if ($LASTEXITCODE -ne 0) { throw "GgmPatcher build failed" }
Write-Host "Built GgmPatcher (enables OpenVR in the game's build settings)"
