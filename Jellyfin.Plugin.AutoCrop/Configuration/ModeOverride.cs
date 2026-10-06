using System;

namespace Jellyfin.Plugin.AutoCrop.Configuration;

/// <summary>The mode a library, series, movie or episode plays with instead of the one it inherits.</summary>
public class ModeOverride
{
    /// <summary>The library (collection folder), series or video it applies to.</summary>
    public Guid Id { get; set; }

    /// <summary>"per-scene", "static" or "off".</summary>
    public string Mode { get; set; } = CropModes.PerScene;
}
