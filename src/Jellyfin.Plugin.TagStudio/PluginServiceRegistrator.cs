using Jellyfin.Plugin.TagStudio.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TagStudio;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<OperationJournal>();
        serviceCollection.AddSingleton<CollectionService>();
        serviceCollection.AddSingleton<MetadataWriter>();
        serviceCollection.AddSingleton<LibraryQueryService>();
        serviceCollection.AddHostedService<PluginPageRegistrar>();
    }
}
