using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Comseg.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Comseg;

/// <summary>
/// Comseg: commercial breaks in DVR recordings, detected with Comskip and
/// published as Jellyfin Commercial media segments. Recordings still being
/// written get their breaks while they record (a patched Comskip re-runs its
/// full analysis every minute of content); finished recordings get a final
/// pass. Clients skip the segments through the standard MediaSegments API.
/// </summary>
public class ComsegPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public ComsegPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the plugin instance (configuration access; services come from DI).
    /// </summary>
    public static ComsegPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public override Guid Id => new Guid("96262b59-95a6-4283-913c-e20e0967b35c");

    /// <inheritdoc />
    public override string Name => "Comseg";

    /// <inheritdoc />
    public override string Description
        => "Commercial segments for DVR recordings, detected with Comskip while they record and once they finish.";

    /// <summary>
    /// Gets the current configuration, or the defaults when the plugin has not
    /// finished loading (services can be constructed before Instance is set).
    /// </summary>
    public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "comseg",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
            },
        };
    }
}
