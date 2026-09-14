using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TagStudio.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Add the edited field to LockedFields so a "Replace all metadata" refresh
    /// cannot revert it. See MetadataService.MergeBaseItemData.
    /// </summary>
    public bool AutoLockEditedFields { get; set; } = true;

    /// <summary>
    /// Semicolon-separated prefixes identifying machine-generated tags
    /// (e.g. those written by the Language Tags plugin). Grouped separately
    /// in the UI and excluded from bulk delete.
    /// </summary>
    public string MachineTagPrefixes { get; set; } = "language_;subtitle_language_";

    /// <summary>
    /// Default state of the "apply to seasons/episodes" toggle.
    /// </summary>
    public bool PropagateToChildrenByDefault { get; set; }

    /// <summary>
    /// How many operation snapshots to retain for undo.
    /// </summary>
    public int SnapshotRetention { get; set; } = 50;

    /// <summary>
    /// Maximum items returned by a single /Query call.
    /// </summary>
    public int MaxQueryPageSize { get; set; } = 500;

    /// <summary>
    /// Semicolon-separated markers identifying collections another plugin generates
    /// (SmartCollections names them "... [Smart]"). Editing one by hand appears to work
    /// until the generator next runs, so these are flagged and refused.
    /// </summary>
    public string ManagedCollectionMarkers { get; set; } = "[Smart]";

    /// <summary>
    /// How many items go into one UpdateItemsAsync call - i.e. one repository
    /// transaction. Larger batches are faster but hold the write open for longer.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// Off by default. Jellyfin runs its metadata savers once per item on any update
    /// that outranks MetadataDownload, so leaving this on writes an NFO sidecar for
    /// every edited item - the dominant cost of a large bulk edit, and pointless here
    /// because Radarr and Sonarr regenerate those files without the tags anyway.
    /// Turn it on only if Jellyfin owns the NFO files for your libraries.
    /// </summary>
    public bool WriteNfoOnEdit { get; set; }
}
