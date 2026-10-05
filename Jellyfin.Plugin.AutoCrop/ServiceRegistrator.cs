using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AutoCrop;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<CropStore>();
        serviceCollection.AddSingleton<CropScanner>();
        serviceCollection.AddSingleton<ScanQueue>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<ScanQueue>());

        // Adds the player script to the web client without the File Transformation plugin.
        serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();
    }
}
