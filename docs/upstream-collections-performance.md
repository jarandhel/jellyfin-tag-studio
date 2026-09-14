# Jellyfin: listing collections is dominated by per-folder unplayed-count queries

Measured on Jellyfin 10.11.5, Windows, against a library with 239 collections.

## Cause

`Folder.FillUserDataDtoValues` runs a recursive unplayed-count query for **every folder in
the result set**:

```csharp
// MediaBrowser.Controller/Entities/Folder.cs
if (SupportsPlayedStatus)
{
    var unplayedQueryResult = GetItems(new InternalItemsQuery(user)
    {
        Recursive = true,
        IsFolder = false,
        IsVirtualItem = false,
        EnableTotalRecordCount = true,
        Limit = 0,
        IsPlayed = false,
        DtoOptions = new DtoOptions(false) { EnableImages = false }
    }).TotalRecordCount;
```

Listing N collections therefore performs N recursive counts. It is reached from
`DtoService.AttachUserSpecificInfo` whenever `EnableUserData` is set, which is the default
for the web client's collections view.

## Measurements

`GET /Items?includeItemTypes=BoxSet&recursive=true&limit=100`:

| | time |
|---|---|
| default | 32.3s |
| `enableUserData=false` | **0.265s** |
| `limit=500&enableUserData=false` (all 239) | **0.244s** |

122x on the same query, same data, one parameter. For contrast, 1000 movies return in
0.63s - the cost is specific to folders.

`enableImages=false` changes nothing (60.2s vs 63.1s), the work is CPU-bound with no disk
reads (58.1s CPU over 60.5s wall, 0 read ops), and EF logs only **292 SQL commands
totalling 0.2s** for a 61s request - the time is in the recursive counting, not the
database.

## Empty collections are the pathological case

Cost is *inversely* correlated with collection size, because a collection with no members
has nothing to bound the recursion:

| cohort | time | per collection |
|---|---|---|
| 46 empty collections | 57.1s | 1.24s |
| 46 non-empty collections | 7.3s | 0.16s |
| 100 smallest (87 members total) | 74.5s | 0.75s |
| 100 largest (5,065 members total) | 11.3s | 0.11s |

An empty folder costs roughly 8x a populated one.

## Suggested fix

Skip the unplayed query when the folder has no children, and bound it to the folder's own
contents otherwise. A folder with nothing in it cannot have unplayed items, so the count is
known without a query.

## Hypotheses eliminated along the way

Recorded so nobody repeats them:

1. **`FindByPath` eager-loading with `DtoOptions(true)`.** Patched `FindLinkedChild` to
   resolve identity via `GetItemIds` then load through the id-cached `GetItemById`, built
   from v10.11.5 and run side by side. Identical results, no timing change.
2. **`BoxSet.LibraryFolderIds` being null**, forcing `GetLibraryFolderIds()` to walk every
   member. Refreshed all 239 collections on a copy and measured both servers
   simultaneously: 35.4/32.8/34.2s refreshed against 30.1/32.3s not.
3. **Member count / serialised blob size.** Contradicted by the inverse correlation.
4. **Unresolvable member paths.** The slow cohort has none (87 of 87 resolve).
5. **Image decoding.** `enableImages=false` makes no difference.

Code reading produced five wrong answers. The profiler settled it in one measurement:
`UNMANAGED_CODE_TIME` at 100% of samples, no disk I/O, negligible SQL - which pointed at
in-memory recursive counting rather than anything in the query or DTO layers.
