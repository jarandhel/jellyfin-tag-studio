#!/usr/bin/env bash
# Builds the SPA, compiles the plugin, and deploys it into Jellyfin.
#
# Linux/macOS counterpart of build.ps1. The plugin itself is platform independent -
# net9.0 with no RuntimeIdentifier - so the only thing that differs is where the
# toolchain and the Jellyfin data directory live.
#
# Deploys into a version-stamped folder rather than overwriting, because Jellyfin holds a
# loaded plugin's DLL open. Jellyfin loads the highest version present, so bump <Version>
# in the csproj for a change that must take effect without restarting.
#
# Override any of these if your toolchain or server lives elsewhere:
#   TAGSTUDIO_DOTNET    path to dotnet
#   TAGSTUDIO_NPM       path to npm
#   TAGSTUDIO_JELLYFIN  Jellyfin data directory (the one containing plugins/)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
proj="$root/src/Jellyfin.Plugin.TagStudio"
guid='71b5006b-7144-4ef0-bee6-ae9f07d740b4'

# A candidate is only accepted if it actually runs. Shims left behind by version
# managers are common and resolve on PATH while pointing at a runtime that no longer
# works, so existence is not the same as usability.
resolve_tool() {
    local override="$1" name="$2"; shift 2
    if [[ -n "$override" ]]; then
        if "$override" --version >/dev/null 2>&1; then echo "$override"; return; fi
        echo "$name was set to '$override' but does not run." >&2; exit 1
    fi
    local candidate
    for candidate in "$(command -v "$name" 2>/dev/null || true)" "$@"; do
        [[ -n "$candidate" && -x "$candidate" ]] || continue
        if "$candidate" --version >/dev/null 2>&1; then echo "$candidate"; return; fi
    done
    echo "Could not find a working $name. Set the matching TAGSTUDIO_* variable." >&2
    exit 1
}

dotnet_bin="$(resolve_tool "${TAGSTUDIO_DOTNET:-}" dotnet "$HOME/.dotnet/dotnet" /usr/share/dotnet/dotnet)"
npm_bin="$(resolve_tool "${TAGSTUDIO_NPM:-}" npm /usr/local/bin/npm)"

# /var/lib/jellyfin is the native package layout; /config is the linuxserver.io image.
jellyfin="${TAGSTUDIO_JELLYFIN:-}"
if [[ -z "$jellyfin" ]]; then
    for candidate in /var/lib/jellyfin /config "$HOME/.local/share/jellyfin"; do
        if [[ -d "$candidate/plugins" || -d "$candidate" ]]; then jellyfin="$candidate"; break; fi
    done
fi
if [[ -z "$jellyfin" || ! -d "$jellyfin" ]]; then
    echo "Jellyfin data directory not found. Set TAGSTUDIO_JELLYFIN." >&2
    exit 1
fi

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$proj/Jellyfin.Plugin.TagStudio.csproj" | head -1)"
dest="$jellyfin/plugins/Tag Studio_$version"

echo "==> building SPA"
(cd "$root/web" && "$npm_bin" run build)

echo "==> building plugin ($version)"
(cd "$proj" && "$dotnet_bin" build -c Release -v minimal)

echo "==> deploying to $dest"
mkdir -p "$dest"
if ! cp "$proj/bin/Release/net9.0/Jellyfin.Plugin.TagStudio.dll" "$dest/" 2>/dev/null; then
    echo "Jellyfin is holding version $version open. Bump <Version> in the csproj and run" >&2
    echo "again, or stop Jellyfin first - a loaded plugin assembly cannot be replaced." >&2
    exit 1
fi

cat > "$dest/meta.json" <<JSON
{
  "category": "General",
  "guid": "$guid",
  "name": "Tag Studio",
  "overview": "Bulk tag, genre and collection management.",
  "description": "Bulk tag, genre and collection management with an iTunes-style column browser.",
  "owner": "local",
  "targetAbi": "10.11.0.0",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)",
  "version": "$version",
  "status": "Active",
  "autoUpdate": false,
  "imagePath": "",
  "assemblies": []
}
JSON

# The plugin prefers a bundle found here over its embedded copy, so UI-only changes take
# effect on a browser refresh instead of a server restart.
live="$jellyfin/plugins/configurations/TagStudio.web"
mkdir -p "$live"
cp "$proj/Web/index.html" "$live/index.html"
echo "  live bundle -> $live/index.html (no restart needed for UI-only changes)"

# Older versions linger while Jellyfin holds their DLL open; clear any it has released.
while IFS= read -r -d '' stale; do
    [[ "$stale" == "$dest" ]] && continue
    if rm -rf "$stale" 2>/dev/null; then
        echo "  removed stale $(basename "$stale")"
    else
        echo "  $(basename "$stale") still in use - will be cleaned up next build"
    fi
done < <(find "$jellyfin/plugins" -maxdepth 1 -type d -name 'Tag Studio_*' -print0)

echo "deployed $version - restart Jellyfin only if the C# changed"
