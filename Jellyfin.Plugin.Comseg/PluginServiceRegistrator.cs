using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Live;
using Jellyfin.Plugin.Comseg.Providers;
using Jellyfin.Plugin.Comseg.Segments;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Comseg;

/// <summary>
/// DI registration. The segment provider must not depend, directly or through
/// anything it takes, on IMediaSegmentManager, IProviderManager, ILibraryManager
/// or IRecordingsManager: the segment manager takes every provider, and those
/// managers depend on one another, so any of them in the provider's constructor
/// graph is a cycle that stops the server (observed on 12.0 through
/// RecordingPaths -> IRecordingsManager -> IProviderManager). The provider and
/// RecordingPaths resolve them per call instead; only the watcher, the store
/// and the controller take them in constructors.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ComskipRunner>();
        serviceCollection.AddSingleton<Workspace>();
        serviceCollection.AddSingleton<ScaledCopy>();
        serviceCollection.AddSingleton<CompletedPass>();
        serviceCollection.AddSingleton<RecordingPaths>();
        serviceCollection.AddSingleton<DetectionRegistry>();
        serviceCollection.AddSingleton<SegmentStore>();
        serviceCollection.AddSingleton<RecordingWatcher>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<RecordingWatcher>());
        serviceCollection.AddSingleton<IMediaSegmentProvider, ComsegSegmentProvider>();
    }
}
