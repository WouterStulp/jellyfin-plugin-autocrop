namespace Jellyfin.Plugin.AutoCrop.Tests;

public class ScriptInjectionTests
{
    private const string Page = "<!DOCTYPE html><html><head><title>Jellyfin</title></head><body></body></html>";

    [Fact]
    public void Inject_AddsTheRelativeTagBeforeHeadClose()
    {
        Assert.Contains(PlayerScript.Tag + "\n</head>", PlayerScript.Inject(Page));
    }

    [Fact]
    public void Inject_AlreadyPresentOrNotHtml_ReturnsInputUnchanged()
    {
        var injected = PlayerScript.Inject(Page);
        const string chunk = "export default function index_html(){}";

        Assert.Same(injected, PlayerScript.Inject(injected));
        Assert.Same(chunk, PlayerScript.Inject(chunk));
    }

    [Fact]
    public void FileTransformationCallback_UsesTheSameTag()
    {
        Assert.Equal(PlayerScript.Inject(Page), ScriptTransformCallback.Transform(new ScriptPatchPayload { Contents = Page }));
    }

    [Theory]
    [InlineData("/web/", "", true)]
    [InlineData("/web/index.html", "", true)]
    [InlineData("/jellyfin/web/", "/jellyfin", true)]
    [InlineData("/jellyfin/web/index.html", "jellyfin/", true)]
    [InlineData("/web/main.js", "", false)]
    [InlineData("/other/web/", "", false)]
    [InlineData("/web/", "/jellyfin", false)]
    public void IsIndexRequest_MatchesOnlyTheIndexPage(string path, string baseUrl, bool expected)
    {
        Assert.Equal(expected, ScriptInjectionStartupFilter.IsIndexRequest(path, baseUrl));
    }
}
