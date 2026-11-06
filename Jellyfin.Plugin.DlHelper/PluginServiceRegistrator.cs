using Jellyfin.Plugin.DlHelper.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DlHelper;

/// <summary>Enregistre le service de surveillance (tourne en arrière-plan avec Jellyfin).</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<Notifier>();
        serviceCollection.AddSingleton<DownloadWatcher>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<DownloadWatcher>());
    }
}
