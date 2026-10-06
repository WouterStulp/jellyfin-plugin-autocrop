using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoCrop;

/// <summary>
/// The player script tag, shared by <see cref="ScriptInjectionStartupFilter"/> and the File
/// Transformation callback so both inject the same tag and each recognises the other's work.
/// </summary>
internal static class PlayerScript
{
    internal const string Marker = "AutoCrop/Web/autocrop.js";

    // Relative to /web/ so it resolves under a server base URL (e.g. /jellyfin/web/). The version in
    // the URL makes browsers fetch the new script after a plugin update instead of a cached copy.
    internal static readonly string Tag =
        $"<script src=\"../AutoCrop/Web/autocrop.js?v={typeof(PlayerScript).Assembly.GetName().Version}\" defer></script>";

    /// <summary>Inserts <see cref="Tag"/> before &lt;/head&gt;, unless it is already there or this isn't an HTML page.</summary>
    internal static string Inject(string html)
    {
        if (html.Contains(Marker, StringComparison.Ordinal))
            return html;

        var headClose = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return headClose < 0 ? html : html.Insert(headClose, Tag + "\n");
    }
}

/// <summary>
/// Injects the player script into the web client's index page at request time, so it works without
/// the File Transformation plugin. A pass-through middleware ahead of Jellyfin's pipeline buffers
/// only GETs of the index page and leaves everything else (and every non-HTML or non-200 response)
/// untouched. Nothing on disk changes. Adapted from Jellyscribe's SidebarScriptStartupFilter (MIT).
/// </summary>
public sealed class ScriptInjectionStartupFilter : IStartupFilter
{
    internal const int MaxPageBytes = 1024 * 1024;

    private readonly ILogger<ScriptInjectionStartupFilter> _logger;
    private int _loggedOnce;

    public ScriptInjectionStartupFilter(ILogger<ScriptInjectionStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };
    }

    internal async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || config.DisableScriptMiddleware
            || !HttpMethods.IsGet(context.Request.Method)
            || !IsIndexRequest(context.Request.Path.Value, ServerBaseUrl(context)))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // A compressed, partial, or 304 response can't be edited, and a 304 would let a copy cached
        // before the plugin was installed keep serving the page without the script.
        var headers = context.Request.Headers;
        headers.Remove("Accept-Encoding");
        headers.Remove("Range");
        headers.Remove("If-Range");
        headers.Remove("If-None-Match");
        headers.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Seek(0, SeekOrigin.Begin);
        var editable = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false)
            && buffer.Length <= MaxPageBytes
            && !context.Response.Headers.ContainsKey("Content-Encoding");
        if (!editable)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, leaveOpen: true))
            html = await reader.ReadToEndAsync().ConfigureAwait(false);

        var injected = PlayerScript.Inject(html);
        if (!ReferenceEquals(injected, html) && Interlocked.Exchange(ref _loggedOnce, 1) == 0)
            _logger.LogInformation("AutoCrop player script injected into the web client");

        var bytes = Encoding.UTF8.GetBytes(injected);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }

    // Resolved per request, so a DI problem can never stop the server's pipeline from building.
    private static string? ServerBaseUrl(HttpContext context)
        => (context.RequestServices?.GetService(typeof(IServerConfigurationManager)) as IConfigurationManager)
            ?.GetNetworkConfiguration().BaseUrl;

    /// <summary>Exactly {base}/web/ or {base}/web/index.html, never another route ending in /web/.</summary>
    internal static bool IsIndexRequest(string? path, string? baseUrl)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var prefix = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (prefix.Length > 0 && prefix[0] != '/')
            prefix = "/" + prefix;

        return string.Equals(path, prefix + "/web/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, prefix + "/web/index.html", StringComparison.OrdinalIgnoreCase);
    }
}
