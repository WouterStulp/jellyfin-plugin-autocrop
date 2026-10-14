using System.Xml.Serialization;
using Jellyfin.Plugin.AutoCrop.Configuration;

namespace Jellyfin.Plugin.AutoCrop.Tests;

public class CropModesTests
{
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly Guid Series = Guid.NewGuid();
    private static readonly Guid Library = Guid.NewGuid();

    private static List<ModeOverride> Overrides(params (Guid Id, string Mode)[] modes)
        => modes.Select(m => new ModeOverride { Id = m.Id, Mode = m.Mode }).ToList();

    [Fact]
    public void Effective_ItemBeatsSeriesBeatsLibraryBeatsServer()
    {
        var scopes = new[] { Item, Series, Library };
        var all = Overrides((Library, "off"), (Series, "whole"), (Item, "theatre"));

        Assert.Equal("theatre", CropModes.Effective(all, "off", scopes));
        Assert.Equal("whole", CropModes.Effective(all.Where(o => o.Id != Item).ToList(), "off", scopes));
        Assert.Equal("off", CropModes.Effective(Overrides((Library, "off")), "theatre", scopes));
        Assert.Equal("whole", CropModes.Effective(Overrides(), "whole", scopes));
        Assert.Equal("whole", CropModes.Effective(Overrides((Guid.NewGuid(), "off")), "whole", scopes));
    }

    [Fact]
    public void Effective_UnknownModes_AreTheatre()
    {
        Assert.Equal("theatre", CropModes.Effective(Overrides(), null, new[] { Item }));
        Assert.Equal("theatre", CropModes.Effective(Overrides((Item, "zoom")), "off", new[] { Item }));
    }

    [Fact]
    public void Effective_ModesFromBeforeThisVersion_MapOntoTheNewOnes()
    {
        // Per scene followed the scenes, theatre is the closest; static was one crop that never cut, which is whole.
        Assert.Equal("theatre", CropModes.Effective(Overrides((Item, "per-scene")), "off", new[] { Item }));
        Assert.Equal("whole", CropModes.Effective(Overrides((Item, "static")), "off", new[] { Item }));
        Assert.Equal("whole", CropModes.Effective(Overrides(), "static", new[] { Item }));
    }

    [Fact]
    public void Effective_LooksUpLibrariesOnlyWhenNeeded()
    {
        var librariesLookedUp = false;
        IEnumerable<Guid> Scopes()
        {
            yield return Item;
            librariesLookedUp = true;
            yield return Library;
        }

        CropModes.Effective(Overrides((Item, "whole")), "off", Scopes());
        Assert.False(librariesLookedUp);
        CropModes.Effective(Overrides(), "off", Scopes());
        Assert.False(librariesLookedUp);
    }

    [Theory]
    [InlineData("theatre", true)]
    [InlineData("whole", true)]
    [InlineData("off", true)]
    [InlineData("per-scene", false)]
    [InlineData("static", false)]
    [InlineData("inherit", false)]
    [InlineData(null, false)]
    public void IsValid_OnlyTheThreeModes(string? mode, bool expected)
    {
        Assert.Equal(expected, CropModes.IsValid(mode));
    }

    [Fact]
    public void Overrides_SurviveTheXmlConfiguration()
    {
        var config = new PluginConfiguration { ModeOverrides = Overrides((Series, "whole"), (Library, "off")) };
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var xml = new StringWriter();

        serializer.Serialize(xml, config);
        var read = (PluginConfiguration)serializer.Deserialize(new StringReader(xml.ToString()))!;

        Assert.Equal(new[] { (Series, "whole"), (Library, "off") }, read.ModeOverrides.Select(o => (o.Id, o.Mode)));
    }
}
