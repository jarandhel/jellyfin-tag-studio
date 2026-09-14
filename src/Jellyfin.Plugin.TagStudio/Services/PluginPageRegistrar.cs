using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TagStudio.Services;

/// <summary>
/// Adds Tag Studio to the Jellyfin sidebar via the Plugin Pages plugin.
///
/// Done entirely by reflection so Tag Studio has no hard dependency on Plugin Pages:
/// if it is missing the page simply is not registered and the UI stays reachable at
/// /TagStudio/index.html.
/// </summary>
public class PluginPageRegistrar : IHostedService
{
    private const string PageId = "tag-studio";
    private const string PagesAssembly = "Jellyfin.Plugin.PluginPages";
    private const string ManagerTypeName = "Jellyfin.Plugin.PluginPages.Library.IPluginPagesManager";
    private const string PageTypeName = "Jellyfin.Plugin.PluginPages.Library.PluginPage";

    /// <summary>The fragment endpoint, not index.html - Plugin Pages injects into an existing page.</summary>
    private const string PageUrl = "/TagStudio/page.html";

    private readonly IServiceProvider _services;
    private readonly ILogger<PluginPageRegistrar> _logger;

    public PluginPageRegistrar(IServiceProvider services, ILogger<PluginPageRegistrar> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Plugin load order is not guaranteed, so give Plugin Pages a chance to appear.
        for (var attempt = 0; attempt < 10 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                if (TryRegister())
                {
                    _logger.LogInformation("Tag Studio registered with Plugin Pages at {Url}", PageUrl);
                    return;
                }
            }
            catch (Exception ex) when (ex is TargetInvocationException or MissingMemberException or InvalidCastException)
            {
                _logger.LogWarning(
                    ex,
                    "Plugin Pages is installed but its API did not match what Tag Studio expected. "
                    + "Open Tag Studio at {Url} instead",
                    "/TagStudio/index.html");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Plugin Pages not available; Tag Studio is reachable directly at /TagStudio/index.html");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool TryRegister()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, PagesAssembly, StringComparison.Ordinal));

        var managerType = assembly?.GetType(ManagerTypeName);
        var pageType = assembly?.GetType(PageTypeName);
        if (managerType is null || pageType is null)
        {
            return false;
        }

        var manager = _services.GetService(managerType);
        if (manager is null)
        {
            return false;
        }

        var idProperty = pageType.GetProperty("Id")
                         ?? throw new MissingMemberException(PageTypeName, "Id");

        // Registration lives in memory, so a re-entry after a reload must not duplicate the entry.
        if (managerType.GetMethod("GetPages")?.Invoke(manager, null) is IEnumerable existing)
        {
            foreach (var page in existing)
            {
                if (page is not null
                    && string.Equals(idProperty.GetValue(page) as string, PageId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        var entry = Activator.CreateInstance(pageType)
                    ?? throw new MissingMemberException(PageTypeName, ".ctor");

        idProperty.SetValue(entry, PageId);
        Set(pageType, entry, "Url", PageUrl);
        Set(pageType, entry, "DisplayText", "Tag Studio");
        Set(pageType, entry, "Icon", "label");

        var register = managerType.GetMethod("RegisterPluginPage")
                       ?? throw new MissingMemberException(ManagerTypeName, "RegisterPluginPage");

        register.Invoke(manager, new[] { entry });
        return true;
    }

    private static void Set(Type type, object instance, string property, string value)
        => (type.GetProperty(property) ?? throw new MissingMemberException(type.FullName, property))
            .SetValue(instance, value);
}
