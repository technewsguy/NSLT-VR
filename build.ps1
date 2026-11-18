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
$src += Join-Path $root "installer\SteamVRTag.cs"   # Steam VR section check (shared with the installer)

& $csc /nologo /noconfig /nostdlib+ /target:library /optimize+ /debug- /warn:4 `
    "/out:$out\NSLTVR.dll" $refArgs $src
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out\NSLTVR.dll"

# GgmPatcher on its own (development use; the installer has the same code built in)
& $csc /nologo /optimize+ "/out:$out\GgmPatcher.exe" (Join-Path $root "patcher\GgmPatcher.cs")
if ($LASTEXITCODE -ne 0) { throw "GgmPatcher build failed" }

# Installer: everything that goes into the game folder, embedded as resources "payload/<path in the game folder>".
$payload = Join-Path $out "payload"
Remove-Item $payload -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$payload\BepInEx\core", "$payload\BepInEx\plugins\NSLTVR\input", "$payload\nslt_Data\Plugins" | Out-Null
Copy-Item "$root\deps\winhttp.dll", "$root\deps\doorstop_config.ini", "$root\deps\.doorstop_version" $payload
Copy-Item "$BepInExCore\*" "$payload\BepInEx\core" -Recurse
Copy-Item "$out\NSLTVR.dll" "$payload\BepInEx\plugins\NSLTVR"
Copy-Item "$root\input\*.json" "$payload\BepInEx\plugins\NSLTVR\input"
Copy-Item "$root\deps\openvr\openvr_api.dll" "$payload\nslt_Data\Plugins"
$resArgs = Get-ChildItem $payload -Recurse -File -Force | ForEach-Object {
    "/resource:$($_.FullName),payload/" + $_.FullName.Substring($payload.Length + 1).Replace('\', '/')
}
$version = [regex]::Match((Get-Content "$root\src\Plugin.cs" -Raw), 'Version = "([^"]+)"').Groups[1].Value
"static partial class NsltVrSetup { internal const string ModVersion = `"$version`"; }" | Set-Content "$out\SetupVersion.cs" -Encoding ASCII
@"
using System.Reflection;
[assembly: AssemblyTitle("NSLT-VR $version Setup")]
[assembly: AssemblyProduct("NSLT-VR")]
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
"@ | Set-Content "$out\SetupAssemblyInfo.cs" -Encoding ASCII
& $csc /nologo /target:winexe /optimize+ /main:NsltVrSetup /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/out:$out\NSLT-VR-Setup.exe" (Join-Path $root "installer\Setup.cs") (Join-Path $root "installer\SteamVRTag.cs") (Join-Path $root "patcher\GgmPatcher.cs") "$out\SetupVersion.cs" "$out\SetupAssemblyInfo.cs" $resArgs
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
Write-Host "Built $out\NSLT-VR-Setup.exe ($([math]::Round((Get-Item "$out\NSLT-VR-Setup.exe").Length / 1MB, 2)) MB)"
