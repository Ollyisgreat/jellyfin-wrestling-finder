using Jellyfin.Plugin.WrestlingFinder.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.WrestlingFinder;

/// <summary>
/// Registers the plugin's services with Jellyfin's dependency injection.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<YtDlpLocator>();
        serviceCollection.AddSingleton<FfmpegLocator>();
        serviceCollection.AddSingleton<EventResolver>();
        serviceCollection.AddSingleton<VideoSearchService>();
        serviceCollection.AddSingleton<DownloadManager>();
    }
}
