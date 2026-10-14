using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AutoCrop.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>The mode films play with: "theatre", "whole" or "off". Viewers can override it in their own browser.</summary>
    public string DefaultMode { get; set; } = CropModes.Theatre;

    /// <summary>
    /// The mode episodes play with. Whole by default: many series use black bars as an effect for a
    /// few scenes, and the theatre crop would cut the rest of the episode down to them.
    /// </summary>
    public string EpisodeDefaultMode { get; set; } = CropModes.Whole;

    /// <summary>
    /// Modes for libraries, series, movies and episodes. An item plays with its own, else its series',
    /// else its library's, else <see cref="DefaultMode"/> or <see cref="EpisodeDefaultMode"/>.
    /// </summary>
    public List<ModeOverride> ModeOverrides { get; set; } = new();

    public double MinimumBarPercent { get; set; } = 1.0;

    /// <summary>
    /// Zooms ASS/SSA subtitles with the picture, so positioned signs stay on their spot. Bitmap subtitles
    /// (PGS, VobSub) never zoom: they are often placed in the black bars.
    /// </summary>
    public bool ZoomStyledSubtitles { get; set; } = true;

    /// <summary>Decode on the GPU Jellyfin is set up for (Dashboard > Playback > Transcoding); the CPU is the fallback.</summary>
    public bool HardwareDecoding { get; set; } = true;

    /// <summary>Settle files whose trickplay thumbnails show no bars at all without decoding them.</summary>
    public bool UseTrickplay { get; set; } = true;

    /// <summary>Kill switch for the index.html rewrite (XML only), in case it ever clashes with another plugin.</summary>
    public bool DisableScriptMiddleware { get; set; }
}
