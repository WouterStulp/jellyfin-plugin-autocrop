using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AutoCrop.Configuration;

namespace Jellyfin.Plugin.AutoCrop;

public static class CropModes
{
    public const string PerScene = "per-scene";
    public const string Static = "static";
    public const string Off = "off";

    public static string Normalize(string? mode)
        => mode is Static or Off ? mode : PerScene;

    public static bool IsValid(string? mode) => mode is PerScene or Static or Off;

    /// <summary>
    /// The mode an item plays with: the first of its <paramref name="scopes"/> (the item, its series,
    /// its libraries, in that order) with an override, else the server default. The scopes are only
    /// enumerated as far as needed, so a library lookup is skipped when the item has its own.
    /// </summary>
    public static string Effective(IReadOnlyList<ModeOverride> overrides, string? serverDefault, IEnumerable<Guid> scopes)
    {
        if (overrides.Count > 0)
        {
            foreach (var scope in scopes)
            {
                if (overrides.FirstOrDefault(o => o.Id == scope) is { } found)
                    return Normalize(found.Mode);
            }
        }

        return Normalize(serverDefault);
    }
}
