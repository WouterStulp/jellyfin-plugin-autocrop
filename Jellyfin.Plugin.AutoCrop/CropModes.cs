using System;

namespace Jellyfin.Plugin.AutoCrop;

public static class CropModes
{
    public const string PerScene = "per-scene";
    public const string Static = "static";
    public const string Off = "off";

    public static string Normalize(string? mode)
        => mode is Static or Off ? mode : PerScene;
}
