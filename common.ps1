# Shared helpers for the NSLT-VR scripts (dot-source: . "$PSScriptRoot\common.ps1").

# Finds the New Super Lucky's Tale install folder through Steam's library list (libraryfolders.vdf).
function Find-GameDir {
    $steam = (Get-ItemProperty HKCU:\Software\Valve\Steam -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $libs = @($steam)
    $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += $m.Groups[1].Value -replace '\\\\', '\' }
    }
    foreach ($lib in ($libs | Select-Object -Unique)) {
        $dir = Join-Path $lib "steamapps\common\New Super Luckys Tale"
        if (Test-Path (Join-Path $dir "nslt.exe")) { return $dir }
    }
    return $null
}

function Resolve-GameDir([string]$GameDir) {
    if ($GameDir) { return $GameDir }
    $found = Find-GameDir
    if (-not $found) { throw "Couldn't find New Super Lucky's Tale. Pass -GameDir 'X:\...\steamapps\common\New Super Luckys Tale'." }
    return $found
}
