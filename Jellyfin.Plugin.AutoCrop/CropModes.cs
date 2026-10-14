using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AutoCrop.Configuration;

namespace Jellyfin.Plugin.AutoCrop;

public static class CropModes
{
    /// <summary>One crop for the whole file, cut to its narrowest recurring shape, as a regular cinema shows an IMAX film.</summary>
    public const string Theatre = "theatre";

    /// <summary>One crop for the whole file that never cuts picture: only bars present in every frame go.</summary>
    public const string Whole = "whole";

    public const string Off = "off";

    /// <summary>
    /// A stored mode as one of the three. Modes from before this version map onto them: "per-scene"
    /// (follow the scenes) is now theatre, "static" (one crop, never cut) is whole.
    /// </summary>
    public static string Normalize(string? mode)
        => mode switch
        {
            Whole or "static" => Whole,
            Off => Off,
            _ => Theatre,
        };

    public static bool IsValid(string? mode) => mode is Theatre or Whole or Off;

    /// <summary>
    /// The mode an item plays with: the first of its <paramref name="scopes"/> (the item, its series,
    /// its libraries, in that order) with an override, else <paramref name="serverDefault"/>. The
    /// scopes are only enumerated as far as needed, so a library lookup is skipped when the item has its own.
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
