using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.TagStudio.Models;
using System.Diagnostics;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TagStudio.Services;

/// <summary>
/// Reads the library for the column browser and the item table.
/// </summary>
public class LibraryQueryService
{
    private static readonly BaseItemKind[] DefaultKinds =
    {
        BaseItemKind.Movie,
        BaseItemKind.Series
    };

    private readonly ILibraryManager _libraryManager;
    private readonly CollectionService _collections;
    private readonly ILogger<LibraryQueryService> _logger;

    public LibraryQueryService(
        ILibraryManager libraryManager,
        CollectionService collections,
        ILogger<LibraryQueryService> logger)
    {
        _libraryManager = libraryManager;
        _collections = collections;
        _logger = logger;
    }

    /// <summary>
    /// BaseItemRepository.ApplyNavigations gates its eager loads on DtoOptions:
    /// ProviderIds -> Provider, Settings -> LockedFields, EnableUserData -> UserData,
    /// EnableImages -> Images. The default DtoOptions asks for all of them, and with
    /// AsSingleQuery that cartesian-joins across the whole library. Ask for nothing
    /// beyond what is actually read.
    /// </summary>
    private static DtoOptions Lean(bool needsLockedFields) => new(false)
    {
        Fields = needsLockedFields ? new[] { ItemFields.Settings } : Array.Empty<ItemFields>(),
        EnableImages = false,
        EnableUserData = false,
        ImageTypeLimit = 0
    };

    public static bool IsMachineName(string name)
    {
        var configured = Plugin.Instance?.Configuration.MachineTagPrefixes;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        foreach (var prefix in configured.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the item carries at least one tag the user actually chose.
    ///
    /// Counting raw emptiness is useless on a library where another plugin tags
    /// everything: with Language Tags writing language_* onto nearly every item,
    /// "no tags at all" was 0 while 154 items in fact had nothing but autotags.
    /// </summary>
    private static bool HasUserTags(BaseItem item)
    {
        var tags = item.Tags;
        if (tags is null || tags.Length == 0)
        {
            return false;
        }

        foreach (var tag in tags)
        {
            if (!IsMachineName(tag))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Groups case and punctuation variants so "Sci-Fi", "sci fi" and "scifi"
    /// land on the same key for typo detection.
    /// </summary>
    public static string NormalizeKey(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        var length = 0;
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }

    private static BaseItemKind[] ResolveKinds(string[] requested)
    {
        if (requested.Length == 0)
        {
            return DefaultKinds;
        }

        var kinds = new List<BaseItemKind>();
        foreach (var name in requested)
        {
            if (Enum.TryParse<BaseItemKind>(name, true, out var kind))
            {
                kinds.Add(kind);
            }
        }

        return kinds.Count == 0 ? DefaultKinds : kinds.ToArray();
    }

    /// <summary>
    /// Builds all four browser columns plus the "untagged" counts in one pass.
    /// </summary>
    public FacetsResponse GetFacets(string[] itemTypes)
    {
        var stopwatch = Stopwatch.StartNew();
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = ResolveKinds(itemTypes),
            Recursive = true,
            DtoOptions = Lean(false)
        });

        var tags = new Dictionary<string, VocabularyEntry>(StringComparer.OrdinalIgnoreCase);
        var genres = new Dictionary<string, VocabularyEntry>(StringComparer.OrdinalIgnoreCase);
        var studios = new Dictionary<string, VocabularyEntry>(StringComparer.OrdinalIgnoreCase);
        var years = new Dictionary<string, VocabularyEntry>(StringComparer.OrdinalIgnoreCase);

        var untagged = 0;
        var noGenre = 0;

        foreach (var item in items)
        {
            Accumulate(tags, item.Tags, FieldKind.Tag, machineAware: true);
            Accumulate(genres, item.Genres, FieldKind.Genre, machineAware: false);
            Accumulate(studios, item.Studios, FieldKind.Tag, machineAware: false);

            if (item.ProductionYear.HasValue)
            {
                Accumulate(
                    years,
                    new[] { item.ProductionYear.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    FieldKind.Tag,
                    machineAware: false);
            }

            if (!HasUserTags(item))
            {
                untagged++;
            }

            if (item.Genres is null || item.Genres.Length == 0)
            {
                noGenre++;
            }
        }

        _logger.LogDebug(
            "Tag Studio facets: {Count} items in {Elapsed}ms",
            items.Count,
            stopwatch.ElapsedMilliseconds);

        return new FacetsResponse
        {
            Tags = ByCountThenName(tags),
            Genres = ByCountThenName(genres),
            Studios = ByCountThenName(studios),
            Years = years.Values
                .OrderByDescending(e => e.Name, StringComparer.Ordinal)
                .ToArray(),
            TotalItems = items.Count,
            UntaggedCount = untagged,
            NoGenreCount = noGenre
        };
    }

    private static VocabularyEntry[] ByCountThenName(Dictionary<string, VocabularyEntry> source)
        => source.Values
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void Accumulate(
        Dictionary<string, VocabularyEntry> into,
        string[]? values,
        FieldKind kind,
        bool machineAware)
    {
        if (values is null)
        {
            return;
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (into.TryGetValue(value, out var existing))
            {
                existing.Count++;
                continue;
            }

            into[value] = new VocabularyEntry
            {
                Name = value,
                Kind = kind,
                Count = 1,
                IsMachine = machineAware && IsMachineName(value),
                NormalizedKey = NormalizeKey(value)
            };
        }
    }

    /// <summary>
    /// Every item carrying the given tag or genre, across all supported types.
    /// Used by the library-wide rename, merge and delete operations.
    /// </summary>
    public Guid[] FindItemsWith(FieldKind field, string value)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = DefaultKinds,
            Recursive = true,
            DtoOptions = Lean(false)
        };

        if (field == FieldKind.Tag)
        {
            query.Tags = new[] { value };
        }
        else
        {
            query.Genres = new[] { value };
        }

        return _libraryManager.GetItemList(query).Select(i => i.Id).ToArray();
    }

    public ItemQueryResponse Query(ItemQueryRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = ResolveKinds(request.ItemTypes),
            Recursive = true,
            OrderBy = BuildOrderBy(request),
            DtoOptions = Lean(true)
        };

        if (request.ParentId.HasValue && request.ParentId.Value != Guid.Empty)
        {
            query.ParentId = request.ParentId.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            query.SearchTerm = request.SearchTerm;
        }

        if (request.Tags.Length > 0)
        {
            query.Tags = request.Tags;
        }

        if (request.Genres.Length > 0)
        {
            query.Genres = request.Genres;
        }

        if (request.Years.Length > 0)
        {
            query.Years = request.Years;
        }

        // Studio and the "untagged"/"no genre" pseudo-filters have no InternalItemsQuery
        // equivalent, so those queries are filtered and paged in memory instead.
        var needsInMemory = request.Studios.Length > 0 || request.UntaggedOnly || request.NoGenreOnly;

        if (!needsInMemory)
        {
            query.StartIndex = request.StartIndex;
            query.Limit = Math.Clamp(request.Limit, 1, MaxPageSize);
            query.EnableTotalRecordCount = true;

            var page = _libraryManager.GetItemsResult(query);
            var memberships = _collections.BuildMembershipIndex();
            _logger.LogDebug(
                "Tag Studio query: {Count} of {Total} in {Elapsed}ms",
                page.Items.Count,
                page.TotalRecordCount,
                stopwatch.ElapsedMilliseconds);

            return new ItemQueryResponse
            {
                Items = page.Items.Select(i => Project(i, memberships)).ToArray(),
                TotalCount = page.TotalRecordCount
            };
        }

        IEnumerable<BaseItem> filtered = _libraryManager.GetItemList(query);

        if (request.UntaggedOnly)
        {
            filtered = filtered.Where(i => !HasUserTags(i));
        }

        if (request.NoGenreOnly)
        {
            filtered = filtered.Where(i => i.Genres is null || i.Genres.Length == 0);
        }

        if (request.Studios.Length > 0)
        {
            filtered = filtered.Where(i =>
                i.Studios is not null
                && i.Studios.Intersect(request.Studios, StringComparer.OrdinalIgnoreCase).Any());
        }

        var materialised = filtered.ToArray();
        var inMemoryMemberships = _collections.BuildMembershipIndex();
        return new ItemQueryResponse
        {
            Items = materialised
                .Skip(request.StartIndex)
                .Take(Math.Clamp(request.Limit, 1, MaxPageSize))
                .Select(i => Project(i, inMemoryMemberships))
                .ToArray(),
            TotalCount = materialised.Length
        };
    }

    private static int MaxPageSize => Plugin.Instance?.Configuration.MaxQueryPageSize ?? 500;

    private static (ItemSortBy, SortOrder)[] BuildOrderBy(ItemQueryRequest request)
    {
        var direction = request.SortDescending ? SortOrder.Descending : SortOrder.Ascending;
        var field = request.SortBy switch
        {
            "DateCreated" => ItemSortBy.DateCreated,
            "ProductionYear" => ItemSortBy.ProductionYear,
            "CommunityRating" => ItemSortBy.CommunityRating,
            _ => ItemSortBy.SortName
        };

        return new[] { (field, direction) };
    }

    private static ItemRow Project(BaseItem item, IReadOnlyDictionary<Guid, List<string>> memberships) => new()
    {
        Id = item.Id,
        Name = item.Name ?? string.Empty,
        Type = item.GetBaseItemKind().ToString(),
        Year = item.ProductionYear,
        Overview = item.Overview ?? string.Empty,
        ParentName = item switch
        {
            Episode episode => episode.SeriesName ?? string.Empty,
            Season season => season.SeriesName ?? string.Empty,
            _ => string.Empty
        },
        IndexNumber = item.IndexNumber,
        ParentIndexNumber = item.ParentIndexNumber,
        Tags = item.Tags ?? Array.Empty<string>(),
        Genres = item.Genres ?? Array.Empty<string>(),
        Studios = item.Studios ?? Array.Empty<string>(),
        Collections = memberships.TryGetValue(item.Id, out var inCollections)
            ? inCollections.ToArray()
            : Array.Empty<string>(),
        DateCreated = item.DateCreated,
        TagsLocked = item.LockedFields.Contains(MetadataField.Tags),
        GenresLocked = item.LockedFields.Contains(MetadataField.Genres)
    };
}
