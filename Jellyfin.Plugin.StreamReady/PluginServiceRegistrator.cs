using Jellyfin.Plugin.StreamReady.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.StreamReady;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<JobStore>();
        serviceCollection.AddSingleton<CompatibilityAnalyzer>();
        serviceCollection.AddSingleton<EncodePlanner>();
        serviceCollection.AddSingleton<FfmpegRunner>();
        serviceCollection.AddSingleton<ReplacementService>();
        serviceCollection.AddSingleton<LibraryScanner>();
        serviceCollection.AddSingleton<EncodeWorker>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<EncodeWorker>());
    }
}
