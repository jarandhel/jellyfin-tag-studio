# Builds the SPA, compiles the plugin, and deploys it into Jellyfin.
# Jellyfin must be restarted afterwards to load the new version.
#
# Deploys into a version-stamped folder rather than overwriting, because Jellyfin
# holds the DLL of a loaded plugin open. Jellyfin picks the highest version present.
$ErrorActionPreference = 'Stop'

$root   = $PSScriptRoot
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
$npm    = "$env:LOCALAPPDATA\nvm\v24.11.1\npm.cmd"
$proj   = "$root\src\Jellyfin.Plugin.TagStudio"
$guid   = '71b5006b-7144-4ef0-bee6-ae9f07d740b4'

$version = ([xml](Get-Content "$proj\Jellyfin.Plugin.TagStudio.csproj")).Project.PropertyGroup.Version |
           Where-Object { $_ } | Select-Object -First 1
$dest = "C:\ProgramData\Jellyfin\Server\plugins\Tag Studio_$version"

Write-Host "==> building SPA" -ForegroundColor Cyan
Push-Location "$root\web"; & $npm run build; Pop-Location

Write-Host "==> building plugin ($version)" -ForegroundColor Cyan
Push-Location $proj; & $dotnet build -c Release -v minimal; Pop-Location

Write-Host "==> deploying to $dest" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item "$proj\bin\Release\net9.0\Jellyfin.Plugin.TagStudio.dll" -Destination $dest -Force

@"
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
"@ | ForEach-Object {
  # Set-Content -Encoding utf8 writes a BOM on Windows PowerShell 5.1, which makes
  # Jellyfin's PluginManager fail to deserialize meta.json.
  [System.IO.File]::WriteAllText("$dest\meta.json", $_, (New-Object System.Text.UTF8Encoding($false)))
}

# Older versions linger because their DLL is locked while Jellyfin runs. Clear any
# that have since been released so the plugins list stays tidy.
Get-ChildItem "C:\ProgramData\Jellyfin\Server\plugins" -Directory -Filter "Tag Studio_*" |
  Where-Object { $_.FullName -ne $dest } |
  ForEach-Object {
    try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop; Write-Host "  removed stale $($_.Name)" }
    catch { Write-Host "  $($_.Name) still in use - will be cleaned up next build" -ForegroundColor DarkYellow }
  }

# Also drop the bundle where the plugin prefers it over its embedded copy, so UI-only
# changes take effect on a browser refresh with no restart.
$live = "C:\ProgramData\Jellyfin\Server\plugins\configurations\TagStudio.web"
New-Item -ItemType Directory -Force -Path $live | Out-Null
Copy-Item "$proj\Web\index.html" -Destination "$live\index.html" -Force
Write-Host "  live bundle -> $live\index.html (no restart needed for UI-only changes)"

Write-Host "deployed $version - restart Jellyfin only if the C# changed" -ForegroundColor Green
