using System;
using System.Collections.Generic;
using Jellyfin.Plugin.WrestlingFinder.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.WrestlingFinder;

/// <summary>
/// Wrestling Finder plugin: resolves a wrestling event name, searches video sites and downloads into Jellyfin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Wrestling Finder";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7c1d2a4e-5b3f-4e8a-9d61-2f0b8c4a9e17");

    /// <inheritdoc />
    public override string Description => "Type a wrestling event, find the real event name, search YouTube / Internet Archive / Dailymotion and download it to your library.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "WrestlingFinder",
                DisplayName = "Wrestling Finder",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuIcon = "sports_mma"
            }
        };
    }
}
