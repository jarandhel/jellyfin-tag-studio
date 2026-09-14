using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TagStudio.Services;

/// <summary>
/// Removes collections that have no members.
///
/// Jellyfin ships a "Clean up collections and playlists" task, but it only strips missing
/// items *out of* a collection - the emptied collection stays. So every time a film leaves
/// the library its collection is left behind as a husk, and they accumulate silently. They
/// are not free either: a collection with no members has nothing to bound its recursive
/// child count, so listing them costs far more than listing collections that are full.
/// </summary>
public class EmptyCollectionCleanupTask : IScheduledTask
{
    private readonly CollectionService _collections;
    private readonly ILogger<EmptyCollectionCleanupTask> _logger;

    public EmptyCollectionCleanupTask(
        CollectionService collections,
        ILogger<EmptyCollectionCleanupTask> logger)
    {
        _collections = collections;
        _logger = logger;
    }

    public string Name => "Delete empty collections";

    public string Key => "TagStudioEmptyCollectionCleanup";

    public string Category => "Tag Studio";

    public string Description =>
        "Removes collections left with no members. Jellyfin's own collection cleanup drops "
        + "missing items out of a collection but leaves the empty collection behind. Until "
        + "\"Auto-delete empty collections\" is enabled in Tag Studio's settings this only "
        + "reports what it would remove.";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Weekly. These appear a few at a time as media leaves the library, so there is
        // nothing to gain from checking more often, and a slow cadence keeps the log
        // readable for anyone reviewing before switching deletion on.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        var armed = Plugin.Instance?.Configuration.AutoDeleteEmptyCollections == true;

        var result = _collections.DeleteEmpty(
            new Models.DeleteEmptyCollectionsRequest { DryRun = !armed },
            cancellationToken);

        progress.Report(90);

        if (result.Deleted.Count == 0)
        {
            _logger.LogInformation("No empty collections found.");
        }
        else if (result.DryRun)
        {
            _logger.LogInformation(
                "{Count} empty collection(s) would be deleted; enable \"Auto-delete empty "
                + "collections\" in Tag Studio's settings to act on this. {Names}",
                result.Deleted.Count,
                string.Join(", ", result.Deleted));
        }
        else
        {
            _logger.LogInformation(
                "Deleted {Count} empty collection(s): {Names}",
                result.Deleted.Count,
                string.Join(", ", result.Deleted));
        }

        // Skips are the interesting output - an offline drive shows up here rather than as
        // a pile of deletions, which is the whole point of refusing to act on them.
        foreach (var skip in result.Skipped)
        {
            _logger.LogInformation("Left alone: {Reason}", skip);
        }

        foreach (var warning in result.Warnings)
        {
            _logger.LogWarning("{Warning}", warning);
        }

        progress.Report(100);
        return Task.CompletedTask;
    }
}
