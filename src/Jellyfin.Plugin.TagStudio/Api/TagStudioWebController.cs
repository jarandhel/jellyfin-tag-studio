using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TagStudio.Api;

/// <summary>
/// Serves the single-file SPA. Anonymous on purpose: the page itself carries no library
/// data and every /TagStudio data endpoint still demands an elevated token, so the shell
/// has to be fetchable before a session header is in play.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("TagStudio")]
public partial class TagStudioWebController : ControllerBase
{
    private const string ResourceName = "Jellyfin.Plugin.TagStudio.Web.index.html";

    private readonly IApplicationPaths _paths;
    private readonly ILogger<TagStudioWebController> _logger;

    public TagStudioWebController(IApplicationPaths paths, ILogger<TagStudioWebController> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// A bundle dropped here overrides the embedded copy. Jellyfin cannot unload a
    /// plugin assembly, so without this every UI tweak would need a server restart;
    /// with it, rebuilding the bundle and copying it here is enough.
    /// </summary>
    private string OverridePath
        => Path.Combine(_paths.PluginConfigurationsPath, "TagStudio.web", "index.html");

    [GeneratedRegex(@"<style[^>]*>.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StyleBlocks();

    [GeneratedRegex(@"<script[^>]*>.*?</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlocks();

    [GeneratedRegex(@"<body[^>]*>(?<body>.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BodyContent();

    /// <summary>The standalone page, for opening Tag Studio directly in a browser tab.</summary>
    [HttpGet("index.html")]
    [HttpGet("")]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetIndex()
    {
        var html = ReadBundle();
        return html is null ? MissingBundle() : Html(html);
    }

    /// <summary>
    /// The same app as an HTML fragment, for Plugin Pages.
    ///
    /// Its container does `createContextualFragment(response)` into an existing Jellyfin
    /// page, which drops html/head/body wrappers, so the document is flattened here.
    /// (createContextualFragment does execute scripts, unlike innerHTML, so the inline
    /// module script still runs once it survives the flattening.)
    ///
    /// Styles and scripts are collected from the whole document rather than from body:
    /// the bundler emits both into head, so extracting body alone would ship an empty
    /// mount point and no application.
    /// </summary>
    [HttpGet("page.html")]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetPageFragment()
    {
        var html = ReadBundle();
        if (html is null)
        {
            return MissingBundle();
        }

        var fragment = new StringBuilder();

        foreach (Match style in StyleBlocks().Matches(html))
        {
            fragment.Append(style.Value).Append('\n');
        }

        // Markup before scripts, so the mount point exists by the time the app runs.
        var body = BodyContent().Match(html);
        var markup = body.Success ? body.Groups["body"].Value : html;
        fragment.Append(ScriptBlocks().Replace(markup, string.Empty)).Append('\n');

        foreach (Match script in ScriptBlocks().Matches(html))
        {
            fragment.Append(script.Value).Append('\n');
        }

        return Html(fragment.ToString());
    }

    private string? ReadBundle()
    {
        var overridePath = OverridePath;
        if (System.IO.File.Exists(overridePath))
        {
            // Only honour an override newer than the assembly itself. build.ps1 writes it
            // after compiling, so a developer's bundle always wins - but installing a
            // release drops a newer DLL beside a stale override, and without this check
            // that old bundle would silently shadow the version just installed.
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var overrideTime = System.IO.File.GetLastWriteTimeUtc(overridePath);

            if (string.IsNullOrEmpty(assemblyPath)
                || overrideTime >= System.IO.File.GetLastWriteTimeUtc(assemblyPath))
            {
                return System.IO.File.ReadAllText(overridePath, Encoding.UTF8);
            }

            _logger.LogWarning(
                "Ignoring the bundle at {Path}: it predates this build of the plugin. "
                + "Delete it, or re-run the build script, to stop this message.",
                overridePath);
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private ActionResult Html(string content)
    {
        // The bundle is replaced on every plugin update, so never let a client hold onto it.
        Response.Headers.CacheControl = "no-store";
        return Content(content, "text/html; charset=utf-8");
    }

    private ActionResult MissingBundle()
        => NotFound($"Embedded resource {ResourceName} is missing from the plugin build.");
}
