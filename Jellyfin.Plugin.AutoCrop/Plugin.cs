using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AutoCrop.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AutoCrop;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "AutoCrop";

    public override Guid Id => Guid.Parse("582167bd-db8e-4e2f-bec7-5dd11b6d82a2");

    public override string Description => "Removes burned-in black bars during playback in the web player.";

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "autocrop",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.configPage.html",
                EnableInMainMenu = true,
                DisplayName = "AutoCrop",
            },
        };
    }
}
