using System.Globalization;
using Jellyfin.Plugin.TagStudio.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TagStudio;

/// <summary>
/// Tag Studio — bulk tag/genre/collection management for Jellyfin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Tag Studio";

    public override Guid Id => Guid.Parse("71b5006b-7144-4ef0-bee6-ae9f07d740b4");

    public override string Description =>
        "Bulk tag, genre and collection management with an iTunes-style column browser.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        // EnableInMainMenu is what puts an admin page in the dashboard navigation and
        // what the plugins list uses to resolve a plugin's settings link:
        //   pages.filter(p => p.PluginId === id).find(p => p.EnableInMainMenu) ?? pages[0]
        yield return new PluginPageInfo
        {
            Name = "TagStudio",
            DisplayName = "Tag Studio",
            EnableInMainMenu = true,
            MenuIcon = "label",
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.mainPage.html",
                GetType().Namespace)
        };

        yield return new PluginPageInfo
        {
            Name = "TagStudioSettings",
            DisplayName = "Tag Studio Settings",
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}
