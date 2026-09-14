# Upstream: listing collections is ~0.4s per collection

Draft for a Jellyfin issue/PR. Not part of the plugin.

## Summary

Listing `BoxSet` items costs roughly 0.4s each and scales linearly, while ordinary item
queries are three orders of magnitude cheaper. **The cause is not yet identified** - the
obvious candidate was measured and ruled out (below).

## The code

`Emby.Server.Implementations/Library/LibraryManager.cs`:

```csharp
public BaseItem? FindByPath(string path, bool? isFolder)
{
    ArgumentException.ThrowIfNullOrEmpty(path);

    var query = new InternalItemsQuery
    {
        Path = path,
        IsFolder = isFolder,
        OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
        Limit = 1,
        DtoOptions = new DtoOptions(true)   // every field
    };

    return GetItemList(query).FirstOrDefault();
}
```

`Jellyfin.Server.Implementations/Item/BaseItemRepository.cs`, `ApplyNavigations`:

```csharp
if (filter.DtoOptions.ContainsField(ItemFields.ProviderIds)) dbQuery = dbQuery.Include(e => e.Provider);
if (filter.DtoOptions.ContainsField(ItemFields.Settings))    dbQuery = dbQuery.Include(e => e.LockedFields);
if (filter.DtoOptions.EnableUserData)                        dbQuery = dbQuery.Include(e => e.UserData);
if (filter.DtoOptions.EnableImages)                          dbQuery = dbQuery.Include(e => e.Images);
```

`MediaBrowser.Controller/Entities/Folder.cs` calls it once per linked child:

```csharp
public List<BaseItem> GetLinkedChildren()
{
    var linkedChildren = LinkedChildren;
    var list = new List<BaseItem>(linkedChildren.Length);
    foreach (var i in linkedChildren)
    {
        var child = GetLinkedChild(i);   // -> FindByPath for path-only entries
        if (child is not null) list.Add(child);
    }
    return list;
}
```

There is an id-keyed cache (`FastConcurrentLru<Guid, BaseItem>`) used by `GetItemById`,
but nothing equivalent for paths.

## Reproduction

Jellyfin 10.11.5, Windows. A library with 239 collections and 5,230 members, all stored
as `<Path>` in `collection.xml` (no `ItemId` or `LibraryItemId` anywhere). 18 smart
collections account for 4,764 of those members; the largest holds 1,811.

`GET /Items?includeItemTypes=BoxSet&recursive=true&limit=N`:

| N | time |
|---|---|
| 1 | 0.27s |
| 5 | 0.90s |
| 50 | 23.1s |
| 100 | 37–40s |

For contrast, `limit=1000` over movies is **0.63s**. Cost scales with the number of
collections materialised, at roughly 0.4s each, and is unaffected by `fields` or whether
a `userId` is supplied.

## Hypothesis tested and ruled out

`FindByPath` runs its lookup with `DtoOptions(true)`, and `ApplyNavigations` gates eager
loads on exactly those flags - so a `Limit = 1` path lookup pulls Images, UserData,
Provider and LockedFields on an `AsSingleQuery`. Since collection membership is stored by
path, `BaseItem.FindLinkedChild` calls it once per member, which looked like an obvious
culprit.

It is not. Patching `FindLinkedChild` to resolve identity via `GetItemIds` (lean
`DtoOptions`) and then load through the id-cached `GetItemById`, built from the v10.11.5
tag and run side by side against a copy of the same library:

| | run 1 | run 2 |
|---|---|---|
| stock 10.11.5 | 62.8s | 52.7s |
| patched | 66.1s | 56.0s |

Identical results, same order, no improvement. (Both figures are inflated relative to the
table above because two servers were competing for the machine.) `FindByPath` is not on
the hot path for *listing* collections.

A corroborating datapoint from the same library: fetching 100 **members of** a large smart
collection - which certainly does resolve `LinkedChildren` - takes **2.4s**, while
**listing** 100 collections takes 37-52s. Whatever dominates is in materialising the
BoxSet itself, not in resolving its members.

## Second hypothesis tested and ruled out

`BoxSet.IsVisible` runs per collection and, when `LibraryFolderIds` is null, walks every
member:

```csharp
var libraryFolderIds = LibraryFolderIds ?? GetLibraryFolderIds();

public Guid[] GetLibraryFolderIds()
    => FlattenItems(this, expandedFolders)
        .SelectMany(LibraryManager.GetCollectionFolders)   // a lookup per member
        .Select(i => i.Id).Distinct().ToArray();
```

`GetCollectionFolders` also re-materialises `GetUserRootFolder().Children` on every call.
Only `BoxSetMetadataService.BeforeSaveInternal` ever populates the cache, so collections
created by plugins never have it.

Ruled out by refreshing all 239 collections on a copy of the library and measuring both
servers at the same moment:

| | run 1 | run 2 | run 3 |
|---|---|---|---|
| copy, all 239 refreshed | 35.4s | 32.8s | 34.2s |
| live, not refreshed | 32.3s | 30.1s | |

No improvement. (Measuring simultaneously matters: run-to-run variance on one server
reaches 25%, enough to manufacture a convincing result from nothing.)

## Where that leaves it

Two plausible causes examined and eliminated. The profile is reproducible and the symptom
is real, but the cause is unidentified. Anyone picking this up can skip `FindByPath` and
`LibraryFolderIds`.

Worth noting separately: nothing in Jellyfin maintains `LibraryFolderIds` when collection
membership changes - `ICollectionManager` never touches it. Since `IsVisible` decides who
can see a collection from that cached value, a collection edited outside a metadata
refresh can carry a stale one. That is a correctness question rather than a performance
one, and it is independent of everything above.
