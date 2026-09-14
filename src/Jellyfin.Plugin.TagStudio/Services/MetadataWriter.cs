using System.Diagnostics;
using Jellyfin.Plugin.TagStudio.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TagStudio.Services;

/// <summary>
/// The write path. Deliberately bypasses POST /Items/{id} (which demands a complete
/// BaseItemDto and fails on partial payloads - jellyfin#10724, jellyfin#12646) and
/// mutates BaseItem through ILibraryManager instead.
/// </summary>
public class MetadataWriter
{
    private readonly ILibraryManager _libraryManager;
    private readonly OperationJournal _journal;
    private readonly CollectionService _collections;
    private readonly ILogger<MetadataWriter> _logger;

    public MetadataWriter(
        ILibraryManager libraryManager,
        OperationJournal journal,
        CollectionService collections,
        ILogger<MetadataWriter> logger)
    {
        _libraryManager = libraryManager;
        _journal = journal;
        _collections = collections;
        _logger = logger;
    }

    /// <summary>
    /// Characters Jellyfin splits tag strings on, taken from each library's
    /// LibraryOptions.CustomTagDelimiters. A tag containing one of these would be
    /// silently torn into pieces on the next refresh, so we reject it up front.
    /// </summary>
    public IReadOnlySet<char> ReservedCharacters()
    {
        var chars = new HashSet<char>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            var delimiters = folder.LibraryOptions?.CustomTagDelimiters;
            if (delimiters is null)
            {
                continue;
            }

            foreach (var delimiter in delimiters)
            {
                foreach (var c in delimiter)
                {
                    chars.Add(c);
                }
            }
        }

        return chars;
    }

    public IReadOnlyList<string> ValidateNames(IEnumerable<string> names)
    {
        var reserved = ReservedCharacters();
        var problems = new List<string>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add("Empty values are not allowed.");
                continue;
            }

            var offending = name.Where(reserved.Contains).Distinct().ToArray();
            if (offending.Length > 0)
            {
                problems.Add(
                    "\"" + name + "\" contains " + string.Join(" ", offending)
                    + ", which this server is configured to split tags on.");
            }
        }

        return problems;
    }

    /// <summary>
    /// LibraryManager.UpdateItemsAsync runs the metadata savers once per item before
    /// its single batched repository write, and MetadataEdit outranks the NFO saver's
    /// minimum - so editing 2,000 items would mean 2,000 sidecar writes. Tag Studio's
    /// durability comes from locked fields plus the operation journal, not from NFO
    /// (Radarr and Sonarr regenerate those files anyway), so by default the savers are
    /// kept out of the loop with a reason that sits below their threshold.
    /// </summary>
    private static ItemUpdateType UpdateReason
        => Plugin.Instance?.Configuration.WriteNfoOnEdit == true
            ? ItemUpdateType.MetadataEdit
            : ItemUpdateType.MetadataImport;

    private static int BatchSize => Math.Clamp(Plugin.Instance?.Configuration.BatchSize ?? 500, 1, 5000);

    public async Task<OperationResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        var warnings = ValidateNames(request.Add).ToList();
        if (warnings.Count > 0)
        {
            return new OperationResult { Warnings = warnings };
        }

        var operation = new Operation
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            Description = Describe(request)
        };

        var pending = new List<BaseItem>();
        var skipped = 0;

        foreach (var itemId in request.ItemIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                skipped++;
                continue;
            }

            var entry = Snapshot(item);
            if (ApplyToItem(item, request.Field, request.Add, request.Remove))
            {
                Capture(entry, item);
                operation.Entries.Add(entry);
                pending.Add(item);
            }
            else
            {
                skipped++;
            }

            if (request.PropagateToChildren && item is Folder folder)
            {
                Propagate(folder, request, operation, pending, cancellationToken);
            }
        }

        var elapsed = await SaveBatchAsync(pending, cancellationToken).ConfigureAwait(false);

        // Journalled only after a successful write, so undo can never promise a
        // rollback for changes that were not persisted.
        if (operation.Entries.Count > 0)
        {
            await _journal.RecordAsync(operation, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Tag Studio {Description}: {Changed} changed, {Skipped} unchanged, in {Elapsed}ms",
            operation.Description,
            pending.Count,
            skipped,
            elapsed);

        return new OperationResult
        {
            OperationId = operation.Id,
            ItemsChanged = pending.Count,
            ItemsSkipped = skipped,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Restores every item touched by an operation to its recorded pre-operation state.
    /// </summary>
    public async Task<OperationResult?> UndoAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var operation = await _journal.LoadAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return null;
        }

        var pending = new List<BaseItem>();
        var skipped = 0;

        foreach (var entry in operation.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = _libraryManager.GetItemById(entry.ItemId);
            if (item is null)
            {
                skipped++;
                continue;
            }

            item.Tags = entry.BeforeTags;
            item.Genres = entry.BeforeGenres;
            pending.Add(item);
        }

        var elapsed = await SaveBatchAsync(pending, cancellationToken).ConfigureAwait(false);

        // Undo has to cover collection membership too, or it would silently restore only
        // half of a mixed operation.
        var revertedMemberships = operation.CollectionChanges.Count > 0
            ? await _collections.RevertAsync(operation.CollectionChanges, cancellationToken).ConfigureAwait(false)
            : 0;

        await _journal.MarkUndoneAsync(operation, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Tag Studio undo of {Description}: {Restored} restored, {Skipped} missing, in {Elapsed}ms",
            operation.Description,
            pending.Count,
            skipped,
            elapsed);

        return new OperationResult
        {
            OperationId = operation.Id,
            ItemsChanged = pending.Count + revertedMemberships,
            ItemsSkipped = skipped
        };
    }

    /// <summary>Journals a collection-only operation so it shows up in undo like any other.</summary>
    public async Task<Guid> RecordCollectionOperationAsync(
        string description,
        IReadOnlyList<Models.CollectionChange> changes,
        CancellationToken cancellationToken)
    {
        var operation = new Operation
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            Description = description,
            CollectionChanges = changes.ToList()
        };

        await _journal.RecordAsync(operation, cancellationToken).ConfigureAwait(false);
        return operation.Id;
    }

    /// <summary>
    /// One repository transaction per parent-group chunk instead of one per item.
    /// Grouping by parent matters because UpdateItemsAsync invalidates that folder's
    /// cached children; batching unrelated parents together would leave stale caches.
    /// </summary>
    private async Task<long> SaveBatchAsync(
        IReadOnlyList<BaseItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return 0;
        }

        var stopwatch = Stopwatch.StartNew();
        var reason = UpdateReason;

        foreach (var group in items.GroupBy(i => i.GetParent()?.Id ?? Guid.Empty))
        {
            var parent = group.First().GetParent();

            foreach (var chunk in group.Chunk(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _libraryManager
                    .UpdateItemsAsync(chunk, parent, reason, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return stopwatch.ElapsedMilliseconds;
    }

    /// <summary>
    /// Mirrors the delta propagation Jellyfin's own ItemUpdateController performs,
    /// including its respect for the child's own LockedFields. Children are collected
    /// rather than written here so they join the same batched save.
    /// </summary>
    private static void Propagate(
        Folder folder,
        ApplyRequest request,
        Operation operation,
        List<BaseItem> pending,
        CancellationToken cancellationToken)
    {
        foreach (var child in folder.GetRecursiveChildren())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsLocked(child, request.Field))
            {
                continue;
            }

            var entry = Snapshot(child);
            if (!ApplyToItem(child, request.Field, request.Add, request.Remove))
            {
                continue;
            }

            Capture(entry, child);
            operation.Entries.Add(entry);
            pending.Add(child);
        }
    }

    private static bool ApplyToItem(BaseItem item, FieldKind field, string[] add, string[] remove)
    {
        var current = Read(item, field);
        var next = current
            .Where(v => !remove.Contains(v, StringComparer.OrdinalIgnoreCase))
            .Concat(add)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (current.SequenceEqual(next, StringComparer.Ordinal))
        {
            return false;
        }

        Write(item, field, next);
        AutoLock(item, field);
        return true;
    }

    /// <summary>
    /// Without this, a "Replace all metadata" refresh reverts the edit:
    /// MetadataService.MergeBaseItemData only preserves a field when it is locked.
    /// </summary>
    private static void AutoLock(BaseItem item, FieldKind field)
    {
        if (Plugin.Instance?.Configuration.AutoLockEditedFields != true)
        {
            return;
        }

        var lockField = field == FieldKind.Tag ? MetadataField.Tags : MetadataField.Genres;
        if (item.LockedFields.Contains(lockField))
        {
            return;
        }

        item.LockedFields = item.LockedFields.Append(lockField).Distinct().ToArray();
    }

    private static bool IsLocked(BaseItem item, FieldKind field)
        => item.LockedFields.Contains(field == FieldKind.Tag ? MetadataField.Tags : MetadataField.Genres);

    private static string[] Read(BaseItem item, FieldKind field)
        => (field == FieldKind.Tag ? item.Tags : item.Genres) ?? Array.Empty<string>();

    private static void Write(BaseItem item, FieldKind field, string[] values)
    {
        if (field == FieldKind.Tag)
        {
            item.Tags = values;
        }
        else
        {
            item.Genres = values;
        }
    }

    private static JournalEntry Snapshot(BaseItem item) => new()
    {
        ItemId = item.Id,
        ItemName = item.Name ?? string.Empty,
        BeforeTags = item.Tags ?? Array.Empty<string>(),
        BeforeGenres = item.Genres ?? Array.Empty<string>(),
        BeforeLockedFields = item.LockedFields.Select(f => f.ToString()).ToArray()
    };

    private static void Capture(JournalEntry entry, BaseItem item)
    {
        entry.AfterTags = item.Tags ?? Array.Empty<string>();
        entry.AfterGenres = item.Genres ?? Array.Empty<string>();
    }

    private static string Describe(ApplyRequest request)
    {
        var parts = new List<string>();
        if (request.Add.Length > 0)
        {
            parts.Add("+" + string.Join(", ", request.Add));
        }

        if (request.Remove.Length > 0)
        {
            parts.Add("-" + string.Join(", ", request.Remove));
        }

        return request.Field + " " + string.Join(" ", parts)
            + " on " + request.ItemIds.Length + " item(s)";
    }
}
