using Jellyfin.Plugin.AutoCrop.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using NSubstitute;

namespace Jellyfin.Plugin.AutoCrop.Tests;

/// <summary>Tests that read Plugin.Instance share this collection, so they never run in parallel.</summary>
[CollectionDefinition("Plugin")]
public class PluginCollection
{
}

internal static class TestPlugin
{
    /// <summary>A fresh Plugin.Instance with default configuration, persisting into <paramref name="dir"/>.</summary>
    public static PluginConfiguration Create(string dir)
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(dir);
        paths.PluginsPath.Returns(dir);
        paths.DataPath.Returns(dir);
        var xml = Substitute.For<IXmlSerializer>();
        xml.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>()).Returns(_ => new PluginConfiguration());
        return new Plugin(paths, xml).Configuration;
    }

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "autocrop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
