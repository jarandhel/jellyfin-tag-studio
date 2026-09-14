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

    private IReadOnlyList<BoxSet> LoadBoxSets()
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive = true,
            DtoOptions = new DtoOptions(false)
            {
                Fields = Array.Empty<MediaBrowser.Model.Querying.ItemFields>(),
                EnableImages = false,
                EnableUserData = false,
                ImageTypeLimit = 0
            }
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
    public IReadOnlyDictionary<Guid, List<string>> BuildMembershipIndex()
    {
        var index = new Dictionary<Guid, List<string>>();

        foreach (var boxSet in LoadBoxSets())
        {
            var name = boxSet.Name ?? string.Empty;
            foreach (var child in boxSet.LinkedChildren ?? Array.Empty<LinkedChild>())
            {
                if (child.ItemId is not { } itemId || itemId == Guid.Empty)
                {
                    continue;
                }

                if (!index.TryGetValue(itemId, out var names))
                {
                    names = new List<string>();
                    index[itemId] = names;
                }

                names.Add(name);
            }
        }

        return index;
    }

    public async Task<OperationResult> ApplyAsync(
        CollectionApplyRequest request,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var byName = LoadBoxSets().ToDictionary(b => b.Name ?? string.Empty, b => b, StringComparer.OrdinalIgnoreCase);
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
            var affected = request.ItemIds.Where(boxSet.ContainsLinkedChildByItemId).ToArray();
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
                var affected = request.ItemIds.Where(id => !boxSet.ContainsLinkedChildByItemId(id)).ToArray();
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
