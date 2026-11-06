using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Zorofin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Zorofin;

/// <summary>
/// Zorofin server plugin. Advertises optional modules to Zorofin apps and serves their routes.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The stable plugin identifier. Never change it after release.</summary>
    public static readonly Guid PluginId = Guid.Parse("e49272bf-d1f8-4d83-8cd4-7b3c7f2a1fce");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="xmlSerializer">Jellyfin XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Zorofin";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Optional server features for Zorofin apps: Requests through your Seerr, popular-this-week statistics, play history with an Activity page, Seerr background jobs, and Zorofin Home rows in place of Home Screen Sections.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            // Shown as its own entry in the Dashboard sidebar, not only under Plugins.
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Web.configPage.html", GetType().Namespace),
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "tune"
            },
            // Charts and the activity log from the play history. The page itself is static; its data routes are admin-only.
            new PluginPageInfo
            {
                Name = "ZorofinActivity",
                DisplayName = "Zorofin activity",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Web.activityPage.html", GetType().Namespace),
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "insights"
            }
        };
    }
}
