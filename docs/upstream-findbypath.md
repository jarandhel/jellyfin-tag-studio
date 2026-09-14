# Upstream: `LibraryManager.FindByPath` requests every field for a `Limit = 1` lookup

Draft for a Jellyfin issue/PR. Not part of the plugin.

## Summary

`LibraryManager.FindByPath` runs an existence lookup with `DtoOptions(true)`, which asks
for every field. `BaseItemRepository.ApplyNavigations` gates its eager loads on exactly
those flags, so a `Limit = 1` path lookup eager-loads images, user data, provider ids and
locked fields on an `AsSingleQuery`.

This is on the hot path for collections. Collection membership is stored by path, so
listing collections resolves one `FindByPath` per member, each an expensive multi-join
query with no path-level cache.

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

## Suggested fix

Narrow the `DtoOptions`. The method returns a `BaseItem` for an existence/identity check;
callers such as `GetLinkedChild` use the result's identity, not its images or user data:

```csharp
DtoOptions = new DtoOptions(false)
{
    EnableImages = false,
    EnableUserData = false,
    ImageTypeLimit = 0
}
```

A path-keyed cache alongside the existing id cache would help further, but needs
invalidation on add/remove/move and is a larger change. The `DtoOptions` narrowing is a
one-line change with no behavioural difference for the lookup itself.

## Corroborating datapoint

The same mistake, made and then fixed in a plugin against the same library: a facets pass
over ~2,200 items took **4.5s** with default `DtoOptions` and **0.4s** asking only for
`ItemFields.Settings`. Roughly a 10x difference from the same four `Include` calls.
