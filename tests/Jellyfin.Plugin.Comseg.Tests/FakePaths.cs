using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Comseg.Tests;

/// <summary>Application paths that all point at one folder.</summary>
internal sealed class FakePaths : IApplicationPaths
{
    public FakePaths(string root) => DataPath = root;

    public string DataPath { get; }

    public string ProgramDataPath => DataPath;

    public string WebPath => DataPath;

    public string ProgramSystemPath => DataPath;

    public string ImageCachePath => DataPath;

    public string PluginsPath => DataPath;

    public string PluginConfigurationsPath => DataPath;

    public string LogDirectoryPath => DataPath;

    public string ConfigurationDirectoryPath => DataPath;

    public string SystemConfigurationFilePath => DataPath;

    public string CachePath => DataPath;

    public string TempDirectory => DataPath;

    public string VirtualDataPath => DataPath;

    public string TrickplayPath => DataPath;

    public string BackupPath => DataPath;

    public void MakeSanityCheckOrThrow()
    {
    }

    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
    {
    }
}
