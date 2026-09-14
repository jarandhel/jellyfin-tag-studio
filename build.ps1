# Builds the SPA, compiles the plugin, and deploys it into Jellyfin.
#
# Deploys into a version-stamped folder rather than overwriting, because Jellyfin holds a
# loaded plugin's DLL open. Jellyfin loads the highest version present, so bump <Version>
# in the csproj for a change that must take effect without a shutdown window.
#
# Override any of these if your toolchain or server lives elsewhere:
#   $env:TAGSTUDIO_DOTNET    path to dotnet.exe
#   $env:TAGSTUDIO_NPM       path to npm(.cmd)
#   $env:TAGSTUDIO_JELLYFIN  Jellyfin data directory (the one containing plugins/)
$ErrorActionPreference = 'Stop'

# A candidate is only accepted if it actually runs. Shims left behind by version
# managers are common and resolve to missing or incompatible runtimes, so finding
# something on PATH is not the same as finding something that works.
function Test-Tool($path) {
    if (-not $path) { return $false }
    try {
        $null = & $path --version 2>&1
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

function Resolve-Tool($override, $name, [string[]]$fallbacks) {
    if ($override) {
        if (Test-Tool $override) { return $override }
        throw "$name was set to '$override' but does not run."
    }

    $candidates = @()
    $onPath = Get-Command $name -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += $fallbacks

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate) -and (Test-Tool $candidate)) { return $candidate }
    }

    throw "Could not find a working $name. Set the matching TAGSTUDIO_* environment variable."
}

$root = $PSScriptRoot
$proj = Join-Path $root 'src/Jellyfin.Plugin.TagStudio'
$guid = '71b5006b-7144-4ef0-bee6-ae9f07d740b4'

$dotnet = Resolve-Tool $env:TAGSTUDIO_DOTNET 'dotnet' @("$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")
$npm = Resolve-Tool $env:TAGSTUDIO_NPM 'npm' @(
    (Get-ChildItem "$env:LOCALAPPDATA\nvm\*\npm.cmd" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName)
)

$jellyfin = if ($env:TAGSTUDIO_JELLYFIN) { $env:TAGSTUDIO_JELLYFIN } else { 'C:\ProgramData\Jellyfin\Server' }
if (-not (Test-Path $jellyfin)) {
    throw "Jellyfin data directory not found at '$jellyfin'. Set the TAGSTUDIO_JELLYFIN environment variable."
}

$version = ([xml](Get-Content (Join-Path $proj 'Jellyfin.Plugin.TagStudio.csproj'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
$dest = Join-Path $jellyfin "plugins\Tag Studio_$version"

Write-Host '==> building SPA' -ForegroundColor Cyan
Push-Location (Join-Path $root 'web'); & $npm run build; Pop-Location

Write-Host "==> building plugin ($version)" -ForegroundColor Cyan
Push-Location $proj; & $dotnet build -c Release -v minimal; Pop-Location

Write-Host "==> deploying to $dest" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $dest | Out-Null
try {
    Copy-Item (Join-Path $proj 'bin/Release/net9.0/Jellyfin.Plugin.TagStudio.dll') -Destination $dest -Force -ErrorAction Stop
} catch [System.IO.IOException] {
    throw "Jellyfin is holding version $version open. Bump <Version> in the csproj and " +
          "run again, or stop Jellyfin first - a loaded plugin assembly cannot be replaced in place."
}

$meta = @"
{
  "category": "General",
  "guid": "$guid",
  "name": "Tag Studio",
  "overview": "Bulk tag, genre and collection management.",
  "description": "Bulk tag, genre and collection management with an iTunes-style column browser.",
  "owner": "local",
  "targetAbi": "10.11.0.0",
  "timestamp": "$(Get-Date -Format 'yyyy-MM-ddTHH:mm:ss.0000000Z')",
  "version": "$version",
  "status": "Active",
  "autoUpdate": false,
  "imagePath": "",
  "assemblies": []
}
"@

# Set-Content -Encoding utf8 writes a BOM on Windows PowerShell 5.1, which makes Jellyfin's
# PluginManager fail to deserialize meta.json.
[System.IO.File]::WriteAllText((Join-Path $dest 'meta.json'), $meta, (New-Object System.Text.UTF8Encoding($false)))

# The plugin prefers a bundle found here over its embedded copy, so UI-only changes take
# effect on a browser refresh instead of a server restart.
$live = Join-Path $jellyfin 'plugins\configurations\TagStudio.web'
New-Item -ItemType Directory -Force -Path $live | Out-Null
Copy-Item (Join-Path $proj 'Web/index.html') -Destination (Join-Path $live 'index.html') -Force
Write-Host "  live bundle -> $live\index.html (no restart needed for UI-only changes)"

# Older versions linger while Jellyfin holds their DLL open; clear any it has released.
Get-ChildItem (Join-Path $jellyfin 'plugins') -Directory -Filter 'Tag Studio_*' |
    Where-Object { $_.FullName -ne $dest } |
    ForEach-Object {
        try {
            Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop
            Write-Host "  removed stale $($_.Name)"
        } catch {
            Write-Host "  $($_.Name) still in use - will be cleaned up next build" -ForegroundColor DarkYellow
        }
    }

Write-Host "deployed $version - restart Jellyfin only if the C# changed" -ForegroundColor Green
