using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TagStudio.Models;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TagStudio.Services;

/// <summary>
/// Collection membership. Unlike tags and genres this is not a field on the item -
/// a BoxSet holds LinkedChildren pointing at its members - so it needs its own
/// read path and its own write path through ICollectionManager.
/// </summary>
public class CollectionService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger<CollectionService> _logger;

    // GetLinkedChildren resolves paths, which is a lookup per member, and the index is
    // rebuilt for every page of a query. Cache it briefly and drop it after a write.
    private static readonly object CacheLock = new();
    private static IReadOnlyDictionary<Guid, List<string>>? _cachedIndex;
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    public CollectionService(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ILogger<CollectionService> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _logger = logger;
    }

    /// <summary>
    /// True for collections another plugin owns and regenerates, matched against
    /// configured markers. Editing one by hand looks like it worked until the
    /// generator next runs and silently reverts it - the same trap as machine tags.
    /// </summary>
    public static bool IsManaged(string name)
    {
        var markers = Plugin.Instance?.Configuration.ManagedCollectionMarkers;
        if (string.IsNullOrWhiteSpace(markers))
        {
            return false;
        }

        foreach (var marker in markers.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static DtoOptions LeanOptions() => new(false)
    {
        Fields = Array.Empty<MediaBrowser.Model.Querying.ItemFields>(),
        EnableImages = false,
        EnableUserData = false,
        ImageTypeLimit = 0
    };

    private IReadOnlyList<BoxSet> LoadBoxSets()
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive = true,
            DtoOptions = LeanOptions()
        }).OfType<BoxSet>().ToArray();

    public IReadOnlyList<CollectionEntry> GetCollections()
        => LoadBoxSets()
            .Select(b => new CollectionEntry
            {
                Id = b.Id,
                Name = b.Name ?? string.Empty,
                ItemCount = b.LinkedChildren?.Length ?? 0,
                IsManaged = IsManaged(b.Name ?? string.Empty)
            })
            .OrderByDescending(c => c.ItemCount)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Reverse index of item -> collection names, built in one pass over the
    /// LinkedChildren of every BoxSet. Checking each item against each collection
    /// individually would be a membership scan per pair; this is one pass over the
    /// memberships that exist.
    /// </summary>
    public void InvalidateMembershipCache()
    {
        lock (CacheLock)
        {
            _cachedIndex = null;
        }
    }

    public IReadOnlyDictionary<Guid, List<string>> BuildMembershipIndex()
    {
        lock (CacheLock)
        {
            if (_cachedIndex is not null && DateTime.UtcNow - _cachedAt < CacheLifetime)
            {
                return _cachedIndex;
            }
        }

        var index = new Dictionary<Guid, List<string>>();
        var paths = BuildPathIndex();

        foreach (var boxSet in LoadBoxSets())
        {
            var name = boxSet.Name ?? string.Empty;
            foreach (var itemId in MemberIds(boxSet, paths))
            {
                if (!index.TryGetValue(itemId, out var names))
                {
                    names = new List<string>();
                    index[itemId] = names;
                }

                names.Add(name);
            }
        }

        lock (CacheLock)
        {
            _cachedIndex = index;
            _cachedAt = DateTime.UtcNow;
        }

        return index;
    }

    /// <summary>
    /// Path -> item id for everything a collection can contain, from one query.
    ///
    /// Collection members are stored as paths, and the obvious resolution -
    /// Folder.GetLinkedChildren() - calls ILibraryManager.FindByPath per member. That
    /// runs a database query each time, with DtoOptions(true), so it eager-loads images,
    /// user data and provider ids for what is a Limit=1 existence check. Across a few
    /// thousand memberships it dominates everything else. One bulk query and a dictionary
    /// does the same job.
    /// </summary>
    private Dictionary<string, Guid> BuildPathIndex()
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[]
            {
                BaseItemKind.Movie,
                BaseItemKind.Series,
                BaseItemKind.Season,
                BaseItemKind.Episode
            },
            Recursive = true,
            DtoOptions = LeanOptions()
        });

        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.Path))
            {
                map.TryAdd(item.Path, item.Id);
            }
        }

        return map;
    }

    /// <summary>
    /// The item ids in a collection.
    ///
    /// A LinkedChild identifies its target by ItemId, LibraryItemId *or* Path, and
    /// Jellyfin's own collection.xml stores nothing but Path:
    ///
    ///   &lt;CollectionItem&gt;&lt;Path&gt;G:\Movies\Dracula (1958)\...mkv&lt;/Path&gt;&lt;/CollectionItem&gt;
    ///
    /// Reading the id fields alone reported the right count while resolving no members at
    /// all. Paths come from the bulk index, falling back to a direct lookup for anything
    /// it does not cover - a collection holding a type outside the index, say.
    /// </summary>
    private IEnumerable<Guid> MemberIds(BoxSet boxSet, IReadOnlyDictionary<string, Guid> paths)
    {
        foreach (var child in boxSet.LinkedChildren ?? Array.Empty<LinkedChild>())
        {
            if (child.ItemId is { } itemId && itemId != Guid.Empty)
            {
                yield return itemId;
                continue;
            }

            if (!string.IsNullOrEmpty(child.LibraryItemId)
                && Guid.TryParse(child.LibraryItemId, out var parsed)
                && parsed != Guid.Empty)
            {
                yield return parsed;
                continue;
            }

            if (string.IsNullOrEmpty(child.Path))
            {
                continue;
            }

            if (paths.TryGetValue(child.Path, out var byPath))
            {
                yield return byPath;
            }
            else if (_libraryManager.FindByPath(child.Path, null) is { } found)
            {
                yield return found.Id;
            }
        }
    }

    public async Task<OperationResult> ApplyAsync(
        CollectionApplyRequest request,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var byName = LoadBoxSets().ToDictionary(b => b.Name ?? string.Empty, b => b, StringComparer.OrdinalIgnoreCase);
        var paths = BuildPathIndex();
        var changes = new List<CollectionChange>();

        foreach (var name in request.RemoveFrom.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byName.TryGetValue(name, out var boxSet))
            {
                warnings.Add($"No collection named \"{name}\".");
                continue;
            }

            if (Refuse(name, warnings))
            {
                continue;
            }

            // Only the items actually in it, so undo does not re-add ones that never were.
            // Uses the same resolver as the index: ContainsLinkedChildByItemId consults
            // ItemId alone and so misses members stored under LibraryItemId.
            var members = MemberIds(boxSet, paths).ToHashSet();
            var affected = request.ItemIds.Where(members.Contains).ToArray();
            if (affected.Length == 0)
            {
                continue;
            }

            await _collectionManager.RemoveFromCollectionAsync(boxSet.Id, affected).ConfigureAwait(false);
            changes.Add(new CollectionChange
            {
                CollectionId = boxSet.Id,
                CollectionName = name,
                Removed = affected
            });
        }

        foreach (var name in request.AddTo.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Refuse(name, warnings))
            {
                continue;
            }

            if (byName.TryGetValue(name, out var boxSet))
            {
                var members = MemberIds(boxSet, paths).ToHashSet();
                var affected = request.ItemIds.Where(id => !members.Contains(id)).ToArray();
                if (affected.Length == 0)
                {
                    continue;
                }

                await _collectionManager.AddToCollectionAsync(boxSet.Id, affected).ConfigureAwait(false);
                changes.Add(new CollectionChange
                {
                    CollectionId = boxSet.Id,
                    CollectionName = name,
                    Added = affected
                });
            }
            else
            {
                var created = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                {
                    Name = name,
                    ItemIdList = request.ItemIds.Select(id => id.ToString("N")).ToArray()
                }).ConfigureAwait(false);

                changes.Add(new CollectionChange
                {
                    CollectionId = created.Id,
                    CollectionName = name,
                    Added = request.ItemIds,
                    Created = true
                });

                _logger.LogInformation("Tag Studio created collection {Name} with {Count} items", name, request.ItemIds.Length);
            }
        }

        InvalidateMembershipCache();

        return new OperationResult
        {
            ItemsChanged = changes.Sum(c => (c.Added?.Length ?? 0) + (c.Removed?.Length ?? 0)),
            Warnings = warnings,
            CollectionChanges = changes
        };
    }

    /// <summary>Inverts a recorded set of changes. Used by undo.</summary>
    public async Task<int> RevertAsync(
        IReadOnlyList<CollectionChange> changes,
        CancellationToken cancellationToken)
    {
        var reverted = 0;

        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (change.Added is { Length: > 0 })
            {
                await _collectionManager.RemoveFromCollectionAsync(change.CollectionId, change.Added).ConfigureAwait(false);
                reverted += change.Added.Length;
            }

            if (change.Removed is { Length: > 0 })
            {
                await _collectionManager.AddToCollectionAsync(change.CollectionId, change.Removed).ConfigureAwait(false);
                reverted += change.Removed.Length;
            }
        }

        InvalidateMembershipCache();
        return reverted;
    }

    private static bool Refuse(string name, List<string> warnings)
    {
        if (!IsManaged(name))
        {
            return false;
        }

        warnings.Add(
            $"\"{name}\" is generated by another plugin; a manual change would be reverted "
            + "the next time it runs. Change the rule that builds it instead.");
        return true;
    }
}
