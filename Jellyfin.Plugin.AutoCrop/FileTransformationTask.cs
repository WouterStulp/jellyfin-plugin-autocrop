using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoCrop;

/// <summary>
/// Registers the player script with the File Transformation plugin when it is installed, for setups
/// where the web client is served by something other than Jellyfin's own pipeline. Retried a few
/// times because File Transformation initialises in its own startup task, in no guaranteed order.
/// Adapted from Jellyscribe's SidebarInjectionTask (MIT).
/// </summary>
public class FileTransformationTask : IScheduledTask
{
    private const int MaxAttempts = 3;

    private readonly ILogger<FileTransformationTask> _logger;

    public FileTransformationTask(ILogger<FileTransformationTask> logger)
    {
        _logger = logger;
    }

    public string Name => "AutoCrop script registration";

    public string Key => "AutoCropFileTransformation";

    public string Description => "Registers the player script with the File Transformation plugin, if installed. The script is also added without it.";

    public string Category => "AutoCrop";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                Register();
                return;
            }
            catch (Exception ex)
            {
                var real = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                if (attempt == MaxAttempts)
                {
                    _logger.LogWarning(real, "File Transformation registration failed: {Message}", real.Message);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void Register()
    {
        var ftAssembly = AssemblyLoadContext.All
            .SelectMany(x => x.Assemblies)
            .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
        if (ftAssembly == null)
        {
            _logger.LogDebug("File Transformation plugin not installed");
            return;
        }

        var register = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")?.GetMethod("RegisterTransformation");

        // The payload must be a JObject from File Transformation's own copy of Newtonsoft.Json.
        var newtonsoft = AssemblyLoadContext.GetLoadContext(ftAssembly)?.Assemblies
                .FirstOrDefault(a => a.GetName().Name == "Newtonsoft.Json")
            ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Newtonsoft.Json");
        var jObjectType = newtonsoft?.GetType("Newtonsoft.Json.Linq.JObject");
        var jPropertyType = newtonsoft?.GetType("Newtonsoft.Json.Linq.JProperty");
        if (register == null || jObjectType == null || jPropertyType == null)
        {
            _logger.LogWarning("File Transformation is installed but its registration API was not found");
            return;
        }

        var payload = Activator.CreateInstance(jObjectType)!;
        var add = jObjectType.GetMethod("Add", new[] { typeof(object) })!;
        void AddProperty(string name, string value)
            => add.Invoke(payload, new[] { Activator.CreateInstance(jPropertyType, name, (object)value)! });

        AddProperty("id", "e0a7f1b6-3c2d-4e58-9a41-6b8c2d7f0e13");
        AddProperty("fileNamePattern", "index.html");
        AddProperty("callbackAssembly", typeof(ScriptTransformCallback).Assembly.FullName!);
        AddProperty("callbackClass", typeof(ScriptTransformCallback).FullName!);
        AddProperty("callbackMethod", nameof(ScriptTransformCallback.Transform));

        register.Invoke(null, new[] { payload });
        _logger.LogInformation("AutoCrop player script registered with File Transformation");
    }
}

public static class ScriptTransformCallback
{
    public static string Transform(ScriptPatchPayload payload) => PlayerScript.Inject(payload.Contents ?? string.Empty);
}

public class ScriptPatchPayload
{
    public string? Contents { get; set; }
}
