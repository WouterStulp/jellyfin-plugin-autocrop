using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AutoCrop.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>"per-scene", "static" or "off"; viewers can override it in their own browser.</summary>
    public string DefaultMode { get; set; } = CropModes.PerScene;

    public double MinimumBarPercent { get; set; } = 1.0;

    public double MinimumSegmentSeconds { get; set; } = 2.0;

    public int TransitionMs { get; set; } = 300;

    /// <summary>Decode on the GPU Jellyfin is set up for (Dashboard > Playback > Transcoding); the CPU is the fallback.</summary>
    public bool HardwareDecoding { get; set; } = true;

    /// <summary>Kill switch for the index.html rewrite (XML only), in case it ever clashes with another plugin.</summary>
    public bool DisableScriptMiddleware { get; set; }
}
