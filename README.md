# Tag Studio

A full-screen bulk tag/genre manager for Jellyfin, modelled on the iTunes 2004–2005 column
browser and Amarok 1.x: filter panes narrow a sortable table, multi-select the rows, edit
tags in bulk from a dock at the bottom.

Built for **Jellyfin 10.11.5** on Windows.

## Why this exists rather than using the metadata editor

Jellyfin's stock editor is one item at a time, and every third-party tagger goes through
`POST /Items/{itemId}` — an endpoint that demands a complete `BaseItemDto` and has a long
history of 400/500 failures on partial payloads
([#10724](https://github.com/jellyfin/jellyfin/issues/10724),
[#12646](https://github.com/jellyfin/jellyfin/issues/12646)).

Tag Studio runs server-side and mutates `BaseItem.Tags` through `ILibraryManager` directly,
so bulk writes never touch that endpoint.

## Design constraints this is built around

These were verified against the 10.11 source and the local server config; they explain
several non-obvious choices.

**1 — Locking is what makes an edit durable.** `MetadataService.MergeBaseItemData` guards
every field:

```csharp
if (!lockedFields.Contains(MetadataField.Tags)) {
    if (replaceData || target.Tags.Length == 0) { target.Tags = source.Tags; }
    else { target.Tags = target.Tags.Concat(source.Tags).Distinct(...).ToArray(); }
}
```

A *Replace all metadata* refresh wipes unlocked tags. So `MetadataWriter` adds
`MetadataField.Tags` (or `.Genres`) to the item's `LockedFields` on every edit. Toggle with
`AutoLockEditedFields` in plugin settings.

**2 — No NFO sidecars, deliberately.** Both libraries here set `MetadataSavers = [Nfo]`,
which per `ProviderManager.IsSaverEnabledForItem` is a whitelist that *overrides*
`SaveLocalMetadata=false` — so Jellyfin's NFO saver is live. But Radarr and Sonarr both have
Kodi/Emby metadata enabled and actively regenerate those files without a `<tag>` element.
Anything written there gets stripped. Durability comes from locking plus the operation
journal instead.

*(Reassuring corollary: `BaseNfoParser` only ever calls `item.AddTag(...)` and never clears
`Tags`, so a tagless Radarr NFO cannot erase tags from the database.)*

**3 — Reserved characters.** `LibraryOptions.CustomTagDelimiters` is `/ | ; \` here, and
Jellyfin splits tag strings on those. `MetadataWriter.ReservedCharacters()` reads the real
delimiters from every library and the UI rejects names containing them.

**4 — Machine-generated tags.** The Language Tags plugin writes `language_*` and
`subtitle_language_*` tags that regenerate on refresh. They are grouped under a collapsed
"Auto-generated" section in the browser and cannot be bulk-deleted. Configurable via
`MachineTagPrefixes`.

This also redefines "untagged". Counting `Tags.Length == 0` is meaningless when another
plugin tags everything - on this library it reported **0 untagged** while **154 items**
(134 movies, 20 series) actually had nothing but autotags. `HasUserTags` ignores anything
matching a machine prefix, so the `(untagged)` pseudo-filter finds items carrying no tag
the user chose. `(no genre)` is genuinely 0 here: TMDB supplies a genre for every item.

## Layout

```
toolbar        search · type preset · tags/genres · filters · undo
column browser Tags | Genres | Years | Studios    OR within, AND across
item table     virtualized, sortable, shift/ctrl multi-select
tag dock       tri-state chips · staged edits · Apply / Revert
```

**Tri-state chips** are the core interaction, lifted from the iTunes multi-item Get Info
panel. A tag on *every* selected item is solid; on *some* it is hatched and shows `3/12`.
Clicking a hatched chip applies it to all; clicking a solid one removes it from all.
Nothing is written until **Apply**.

## Operations

| | |
|---|---|
| Bulk add/remove | Tag dock, on the current selection |
| Rename / merge | *Manage tags…* — renaming onto an existing name merges |
| Delete everywhere | *Manage tags…* — machine tags are blocked |
| Duplicate detection | Values matching after case/punctuation stripping are flagged |
| Undo | Toolbar or `Ctrl+Z`; every operation is journalled before/after |

### How a bulk write is applied

Edits are collected, then written through `ILibraryManager.UpdateItemsAsync`, which
issues **one repository transaction per batch** rather than one per item:

```csharp
foreach (var item in items) { ... await RunMetadataSavers(item, updateReason); ... }
_itemRepository.SaveItems(items, cancellationToken);   // once for the whole batch
if (parent is Folder folder) { folder.Children = null; ... }
```

Items are grouped by parent before batching, because that call also invalidates the
parent folder's cached children - batching unrelated parents together would leave
stale caches behind. Batch size is configurable (`BatchSize`, default 500).

The database is not the expensive part, though. `RunMetadataSavers` runs **per item**,
and `ItemUpdateType.MetadataEdit` (16) outranks the NFO saver's minimum (8) - so a
naive implementation writes an NFO sidecar for every edited item. On a select-all that
is thousands of disk writes, and pointless here, since Radarr and Sonarr regenerate
those files without the tags (see constraint 2). So updates are issued as
`MetadataImport`, which sits below the saver threshold and skips them entirely.
Set `WriteNfoOnEdit` if Jellyfin genuinely owns the NFO files for your libraries.

The operation journal is written only *after* a successful save, so undo can never
offer to roll back changes that were not persisted.

Child propagation (series → seasons/episodes) mirrors Jellyfin's own delta logic from
`ItemUpdateController`, respecting each child's `LockedFields`. It is an explicit
per-operation toggle, not a hidden default.

### Keyboard

`/` search · `Ctrl+A` select all · `Ctrl+Z` undo · `Esc` clear selection ·
click / ctrl-click / shift-click in both the panes and the table

## Build

```powershell
.\build.ps1
```

Builds the SPA, compiles the plugin, and deploys to
`C:\ProgramData\Jellyfin\Server\plugins\Tag Studio_<version>`. **Restart Jellyfin afterwards.**

The deploy is version-stamped rather than an overwrite: Jellyfin holds a loaded plugin's
DLL open, so a rebuild cannot replace it in place. Jellyfin loads the highest version
present, and the script removes older folders once Jellyfin has released them - so bump
`<Version>` in the csproj when a change must take effect without a shutdown window.

Requires the .NET 9 SDK (`%LOCALAPPDATA%\Microsoft\dotnet`) and Node
(`%LOCALAPPDATA%\nvm\v24.11.1`) — adjust the paths at the top of `build.ps1` if those move.

## Layout on disk

```
src/Jellyfin.Plugin.TagStudio/
  Api/TagStudioController.cs       JSON API, RequiresElevation
  Api/TagStudioWebController.cs    serves the SPA shell, anonymous
  Services/MetadataWriter.cs       the write path + auto-locking
  Services/LibraryQueryService.cs  facets and item queries
  Services/OperationJournal.cs     before/after snapshots for undo
  Web/index.html                   built SPA (generated, gitignored)
web/                               Preact + Vite source, bundled to one file
```

## API

All under `/TagStudio`, all requiring an elevated token except the shell.

| Method | Route | Purpose |
|---|---|---|
| GET | `/Facets` | tags, genres, years, studios with counts |
| GET | `/ReservedCharacters` | delimiter characters to reject |
| POST | `/Query` | filtered, sorted, paged item rows |
| POST | `/Apply` | bulk add/remove on an item set |
| POST | `/Rename` | library-wide rename or merge |
| POST | `/Delete` | remove a value everywhere |
| GET | `/History` | recent operations |
| POST | `/Undo/{id}` | restore an operation's pre-state |

## How the UI is reached

Primary route is Jellyfin's own plugin page mechanism — no injection, no third-party
plugin in the path. `Plugin.GetPages()` returns a page with `EnableInMainMenu = true`,
which is what the dashboard uses both for the navigation entry and for resolving a
plugin's settings link:

```js
// jellyfin-web plugins chunk
var r = pages.filter(p => p.PluginId === id);
return r.length === 1 ? r[0] : r.find(p => p.EnableInMainMenu) || r[0];
```

Two pages are registered: `TagStudio` (the app, `EnableInMainMenu`) and
`TagStudioSettings` (the options form).

Plugin Pages registration is also kept, but it is secondary: that plugin serves the
*user* drawer (`/PluginPages/User`), and Tag Studio is an admin tool.

### Why the app is fetched rather than inlined

`mainPage.html` is a small embedded loader that pulls `/TagStudio/page.html` and inserts
it with `createContextualFragment`, which — unlike `innerHTML` — executes scripts. That
keeps the bundle in one place and works in any host container.

`/TagStudio/page.html` flattens the bundle to styles plus body content, because the
bundler emits *both* the `<style>` and the `<script type="module">` into `<head>`, and any
host that strips `html`/`head`/`body` wrappers would otherwise drop the entire
application. `/TagStudio/index.html` stays the full standalone document.

Because the injected app sits in a container with no resolvable height, the root element
measures its own offset and sizes itself to the viewport on mount and on resize.

## Not yet built

- Collections bulk add/remove (planned; needs `ICollectionManager`)
- Snapshot export/import for full restore after a library rebuild
