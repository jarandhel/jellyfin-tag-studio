# Jellyfin: listing collections is slow for two independent reasons

Measured on Jellyfin 10.11.5, Windows, 12 logical cores, against a library of 12,868 items
with 239 collections.

Listing collections took 32-80s. There turned out to be **two unrelated causes**, and
neither fix helps the other. Both are reproducible with a single variable changed.

| configuration | `GET /Items?includeItemTypes=BoxSet&limit=100` |
|---|---|
| stock, `CacheSize` 1,200 (the default here) | 32-80s |
| empty-folder guard only | ~12s |
| `CacheSize` 30,000 only | 45-53s |
| **empty-folder guard + `CacheSize` 30,000** | **0.73s** |
| excluding `BoxSet` from `SupportsUserDataFromChildren` | 0.064s (drops three fields) |

Neither fix alone is sufficient. The two causes are orthogonal.

## Where the time goes

`Folder.FillUserDataDtoValues` runs a recursive unplayed-count query for **every folder in
the result set**, reached from `DtoService.AttachUserSpecificInfo` whenever `EnableUserData`
is set - the default for the web client's collections view:

```csharp
// MediaBrowser.Controller/Entities/Folder.cs
if (SupportsPlayedStatus)
{
    var unplayedQueryResult = GetItems(new InternalItemsQuery(user)
    {
        Recursive = true, IsFolder = false, IsVirtualItem = false,
        EnableTotalRecordCount = true, Limit = 0, IsPlayed = false,
        DtoOptions = new DtoOptions(false) { EnableImages = false }
    }).TotalRecordCount;
```

Listing N collections performs N recursive counts. That is the shared entry point. What
makes an individual count expensive differs by collection.

## Cause 1: the item cache is sized by CPU count

For a collection **with** members, `RequiresPostFiltering` takes an in-memory path that
resolves every linked child through `BaseItem.GetLinkedChild` -> `LibraryManager.GetItemById`.
That checks an LRU and falls through to a single-row database read on a miss:

```csharp
// Emby.Server.Implementations/Library/LibraryManager.cs
_cache = new FastConcurrentLru<Guid, BaseItem>(_configurationManager.Configuration.CacheSize);

// MediaBrowser.Model/Configuration/ServerConfiguration.cs
public int CacheSize { get; set; } = Environment.ProcessorCount * 100;
```

**The cache is sized by processor count, which is unrelated to the size of the library.**
On a 12-core machine that is 1,200 entries. The largest collection here holds 10,746
members, so walking it is 9x oversubscribed: it evicts its own working set on every pass,
the hit rate collapses, and each child costs a database round-trip - about 660us per child.

Sampled managed stacks during a 19s request, 3 of 3 in-flight samples:

```
Jellyfin.Server.Implementations!BaseItemRepository.RetrieveItem(Guid)
MediaBrowser.Controller!BaseItem.GetLinkedChild(LinkedChild)
MediaBrowser.Controller!Folder.GetLinkedChildren(User)
MediaBrowser.Controller!Folder.AddChildren(...)
MediaBrowser.Controller!BoxSet.GetRecursiveChildren(...)
MediaBrowser.Controller!Folder.FillUserDataDtoValues(...)
```

Same binary, only `CacheSize` changed - 4 collections holding 20,651 members between them:

| | cache 1,200 | cache 30,000 |
|---|---|---|
| rep 1 | 12.41s | 8.85s (cold, filling cache) |
| rep 2 | 12.26s | **0.243s** |
| rep 3 | 10.99s | **0.206s** |

57x once warm, from configuration alone. Resident memory did not increase.

`CacheSize` is a documented `ServerConfiguration` property, settable in `system.xml`,
though not exposed in the dashboard.

## Cause 2: empty collections take an unbounded recursive scan

`RequiresPostFiltering` only takes the bounded in-memory path when there is something to
bound it:

```csharp
if (LinkedChildren.Length > 0)
{
    if (this is not ICollectionFolder) { return true; }   // in-memory walk
}
```

A non-legacy `BoxSet`'s members *are* its `LinkedChildren` - `BoxSet.LoadChildren()` returns
`[]` with the comment "Save a trip to the database". So a collection with no members fails
that test, falls through to `LibraryManager.GetItemsResult` with `Recursive = true`, and
performs a recursive descendant scan that can only ever return zero.

Cost is therefore *inversely* correlated with collection size:

| cohort | time | per collection |
|---|---|---|
| 46 empty collections | 57.1s | 1.24s |
| 46 non-empty collections | 7.3s | 0.16s |

Raising `CacheSize` does **not** help this case (125s at 30,000 vs 113s at 1,200) - there
are no children to cache.

## Fixes, in order of preference

1. **Raise `CacheSize`** to exceed the library item count (`<CacheSize>25000</CacheSize>`
   for ~12,900 items). Configuration only, no patch, benefits every `GetItemById` in the
   server. Fixes cause 1.
2. **Delete empty collections**, or skip the count for folders known to be empty. A
   `protected virtual bool IsKnownEmpty` on `Folder`, overridden in `BoxSet` as
   `LinkedChildren.Length == 0 && !IsLegacyBoxSet`, lets `FillUserDataDtoValues` fill
   `UnplayedItemCount = 0` / `Played = true` / `RecursiveItemCount = 0` directly. Verified
   byte-identical to stock across all 46 empty collections. Fixes cause 2.
3. **Batch the linked-child resolution.** `Folder.GetLinkedChildren` issues one
   `GetItemById` per child; it could issue one `GetItemList` for all of them. This fixes
   cause 1 independently of cache size and is the proper upstream change.
4. **Exclude `BoxSet` from `SupportsUserDataFromChildren`**, alongside `ICollectionFolder`,
   `UserView`, `UserRootFolder` and `Channel`, which are already excluded there with the
   comment "These are just far too slow." Fastest option (0.064s) and only 8 lines, but it
   drops `UnplayedItemCount`, `PlayedPercentage` and `RecursiveItemCount` on collections -
   the unwatched badge on a collection tile.

## Corrections to an earlier version of this document

Recorded because the wrong conclusions were stated confidently and with numbers.

- **"The time is in the recursive counting, not the database."** Wrong. It is the database:
  one single-row read per linked child. This was inferred from EF logging reporting 292 SQL
  commands totalling 0.2s in a 61s request, which did not account for the repository's own
  per-item reads. Sampled stacks contradict it directly.
- **`UNMANAGED_CODE_TIME` at 100% of samples** was read as "CPU-bound in managed recursion."
  It actually reflects stacks that failed to resolve; it is not evidence of where time went.
  `dotnet-stack report` resolves managed frames properly and should have been used first.
- **Hypothesis 1 below was recorded as eliminated.** It was aimed at the right area - linked
  child resolution - but at the wrong level. Patching `FindLinkedChild` changed nothing
  because the cost is in `GetItemById` missing its cache one frame above it.
- **"Skip the unplayed query when the folder has no children"** was offered as *the* fix. It
  addresses only cause 2, and leaves the page at ~12s.

## Hypotheses eliminated

1. **`FindByPath` eager-loading with `DtoOptions(true)`.** Patched `FindLinkedChild` to
   resolve via `GetItemIds` then `GetItemById`; no timing change. See correction above -
   right area, wrong level.
2. **`BoxSet.LibraryFolderIds` being null**, forcing `GetLibraryFolderIds()` to walk every
   member. Refreshed all 239 collections on a copy and measured both servers
   simultaneously: 35.4/32.8/34.2s refreshed against 30.1/32.3s not.
3. **Member count / serialised blob size.** Contradicted by the inverse correlation.
4. **Unresolvable member paths.** The slow cohort has none (87 of 87 resolve).
5. **Image decoding.** `enableImages=false` makes no difference (60.2s vs 63.1s).

## Method note

Measure both servers interleaved in the same run. Comparing against a baseline captured
earlier produced a spurious 27% improvement once, and the stock server's own timings drifted
from 32s to 80s across a session as the machine took on other load. Check
`curl -w '%{size_download}'` on every timing: a connection failure returns in ~2s and looks
like a fast response.
