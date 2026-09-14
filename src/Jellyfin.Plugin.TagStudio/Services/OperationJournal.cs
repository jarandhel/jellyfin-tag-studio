using System.Text.Json;
using Jellyfin.Plugin.TagStudio.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TagStudio.Services;

public class JournalEntry
{
    public Guid ItemId { get; set; }

    public string ItemName { get; set; } = string.Empty;

    public string[] BeforeTags { get; set; } = Array.Empty<string>();

    public string[] AfterTags { get; set; } = Array.Empty<string>();

    public string[] BeforeGenres { get; set; } = Array.Empty<string>();

    public string[] AfterGenres { get; set; } = Array.Empty<string>();

    public string[] BeforeLockedFields { get; set; } = Array.Empty<string>();
}

public class Operation
{
    public Guid Id { get; set; }

    public DateTime Timestamp { get; set; }

    public string Description { get; set; } = string.Empty;

    public bool Undone { get; set; }

    public List<JournalEntry> Entries { get; set; } = new();

    /// <summary>
    /// Collection membership changes. Separate from Entries because membership lives on
    /// the BoxSet, not the item, so it cannot be captured as a before/after field value.
    /// </summary>
    public List<Models.CollectionChange> CollectionChanges { get; set; } = new();
}

/// <summary>
/// Records a before/after snapshot of every bulk operation so it can be undone,
/// and so tags survive a library rebuild even though we never write NFO sidecars.
/// </summary>
public class OperationJournal
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly ILogger<OperationJournal> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public OperationJournal(ILogger<OperationJournal> logger)
    {
        _logger = logger;
    }

    private static string JournalDirectory
    {
        get
        {
            var root = Plugin.Instance?.DataFolderPath
                       ?? Path.Combine(Path.GetTempPath(), "TagStudio");
            var dir = Path.Combine(root, "operations");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string PathFor(Guid operationId)
        => Path.Combine(JournalDirectory, operationId.ToString("N") + ".json");

    public async Task RecordAsync(Operation operation, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.Create(PathFor(operation.Id));
            await JsonSerializer.SerializeAsync(stream, operation, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            Prune();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Operation?> LoadAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var path = PathFor(operationId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Operation>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkUndoneAsync(Operation operation, CancellationToken cancellationToken)
    {
        operation.Undone = true;
        await RecordAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<Operation> ListRecent(int take)
    {
        var results = new List<Operation>();
        foreach (var file in new DirectoryInfo(JournalDirectory)
                     .GetFiles("*.json")
                     .OrderByDescending(f => f.LastWriteTimeUtc)
                     .Take(take))
        {
            try
            {
                var op = JsonSerializer.Deserialize<Operation>(File.ReadAllText(file.FullName), SerializerOptions);
                if (op is not null)
                {
                    // Entries are not needed for the history list and can be large.
                    op.Entries = new List<JournalEntry>();
                    results.Add(op);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable journal file {File}", file.Name);
            }
        }

        return results;
    }

    private void Prune()
    {
        var retention = Plugin.Instance?.Configuration.SnapshotRetention ?? 50;
        if (retention <= 0)
        {
            return;
        }

        foreach (var stale in new DirectoryInfo(JournalDirectory)
                     .GetFiles("*.json")
                     .OrderByDescending(f => f.LastWriteTimeUtc)
                     .Skip(retention))
        {
            try
            {
                stale.Delete();
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not prune journal file {File}", stale.Name);
            }
        }
    }
}
