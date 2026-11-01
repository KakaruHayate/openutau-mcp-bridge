using System.Reflection;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>
/// Builds the "connect me" prompt and hands it to the user. The text is generated at
/// copy time so it can carry the real port and versions instead of a stale template.
/// </summary>
internal static class AgentPrompt
{
    public const string FileName = "mcp-bridge-prompt.txt";

    public static string Build()
    {
        var status = StatusReport.Build();
        var appVersion = status["app"]?["version"]?.GetValue<string>() ?? "unknown";
        var runtime = status["app"]?["runtime"]?.GetValue<string>() ?? "unknown";
        var bridgeVersion = BridgeInfo.Version;
        var endpoint = BridgeController.Endpoint;
        var clientConfig = "{\"mcpServers\":{\"openutau\":{\"type\":\"http\",\"url\":\"" + endpoint + "\"}}}";

        return $$"""
Connect this machine to the OpenUtau MCP bridge (it is already running).

Endpoint : {{endpoint}}
OpenUtau : {{appVersion}} (.NET {{runtime}})
Bridge   : {{bridgeVersion}} 路 protocol {{BridgeInfo.Protocol}} 路 net10.0

1. Register the endpoint with your MCP client:
   - Codex        : codex mcp add openutau --url {{endpoint}}
   - Claude Code  : claude mcp add --transport http openutau {{endpoint}}
   - anything else: add {{clientConfig}} to your config

2. Call `openutau_status` before anything else.
   - If compatibility.ok is false, stop and quote compatibility.reasons to the user.
   - If it is true, tell the user the connection works (app version + revision) before editing.

3. Editing rules: every write carries `expectedRevision`; batches default to `dryRun:true`;
   one call is one undo entry; never save the project unless the user asked for it.

If the endpoint is unreachable (no tools visible):
   - read the status file next to the bridge DLL: {{StatusFile.FileName}}
   - if that file does not exist, search OpenUtau's Logs folder for "McpBridge"
   - then report both to the user and open an issue at
     https://github.com/KakaruHayate/openutau-mcp-bridge/issues

Do not rebuild the DLL from source: a self-built build usually targets the wrong
.NET version and will fail to load.
""";
    }

    /// <summary>Writes the prompt next to the plugin and tries to put it on the clipboard.</summary>
    public static string Deliver()
    {
        var text = Build();
        var path = Path.Combine(StatusFile.Directory, FileName);
        try
        {
            System.IO.Directory.CreateDirectory(StatusFile.Directory);
            File.WriteAllText(path, text);
        }
        catch (Exception e)
        {
            StatusFile.AppendError("prompt-write", e);
        }

        var (copied, detail) = TryCopyToClipboard(text);
        if (!copied)
        {
            StatusFile.AppendError("clipboard-unavailable", new InvalidOperationException(detail));
        }
        return copied
            ? $"MCP bridge running at {BridgeController.Endpoint} - install prompt copied to clipboard."
            : $"MCP bridge running at {BridgeController.Endpoint} - prompt saved to {path} (clipboard unavailable: {detail}).";
    }

    /// <summary>
    /// Avalonia is not referenced at compile time: the plugin only needs its clipboard, and a
    /// hard reference would tie this DLL to the host's Avalonia version. Every lookup is
    /// interface-aware because several of these members are explicit interface implementations,
    /// and the clipboard task is pumped instead of awaited: blocking the UI thread would
    /// deadlock the very dispatcher the task needs.
    /// </summary>
    private static (bool Ok, string Detail) TryCopyToClipboard(string text)
    {
        var trail = new List<string>();
        try
        {
            var applicationType = FindLoadedType("Avalonia.Application");
            var current = StaticMember(applicationType, "Current");
            var lifetime = Member(current, "ApplicationLifetime");
            var mainWindow = Member(lifetime, "MainWindow");
            var clipboard = Member(mainWindow, "Clipboard");
            trail.Add($"app={applicationType != null} current={current != null} lifetime={lifetime?.GetType().Name ?? "null"} window={mainWindow != null} clipboard={clipboard != null}");

            if (clipboard == null)
            {
                return (false, "no Avalonia clipboard on the main window");
            }

            var setText = Method(clipboard.GetType(), "SetTextAsync", typeof(string));
            var result = setText != null
                ? setText.Invoke(clipboard, new object[] { text })
                : InvokeExtension(clipboard, text);
            if (result == null)
            {
                return (false, "no SetTextAsync on IClipboard or ClipboardExtensions");
            }

            if (result is Task task)
            {
                var completed = PumpUntil(task, TimeSpan.FromSeconds(2));
                trail.Add($"task completed={completed} faulted={task.IsFaulted}");
                if (!completed || task.IsFaulted)
                {
                    StatusFile.AppendError("clipboard-task", task.Exception is { } fault ? (Exception)fault : new TimeoutException("clipboard task did not complete"));
                    return (false, string.Join("; ", trail));
                }
            }

            return (true, "ok");
        }
        catch (Exception e)
        {
            StatusFile.AppendError("clipboard", e);
            trail.Add(e.GetType().Name + ": " + e.Message);
            return (false, string.Join("; ", trail));
        }
    }

    /// <summary>Runs queued dispatcher jobs until the task settles, so an async clipboard call can finish.</summary>
    private static bool PumpUntil(Task task, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            RunDispatcherJobs();
            Thread.Sleep(5);
        }
        return task.IsCompleted;
    }

    private static void RunDispatcherJobs()
    {
        try
        {
            var dispatcherType = FindLoadedType("Avalonia.Threading.Dispatcher");
            var uiThread = StaticMember(dispatcherType, "UIThread");
            if (uiThread == null)
            {
                return;
            }
            var runJobs = Method(uiThread.GetType(), "RunJobs");
            runJobs?.Invoke(uiThread, null);
        }
        catch
        {
            // Pumping is best effort; the timeout decides the outcome.
        }
    }

    private static object? StaticMember(Type? type, string name) =>
        type?.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

    /// <summary>Property lookup that also sees explicit interface implementations.</summary>
    private static object? Member(object? target, string name)
    {
        if (target == null)
        {
            return null;
        }
        var type = target.GetType();
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property != null)
        {
            return property.GetValue(target);
        }
        foreach (var contract in type.GetInterfaces())
        {
            property = contract.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
            {
                return property.GetValue(target);
            }
        }
        return null;
    }

    /// <summary>
    /// Avalonia 12 replaced the IClipboard text helpers with extension methods on
    /// ClipBoardExtensions, so a missing instance method is not a failure by itself.
    /// </summary>
    private static object? InvokeExtension(object clipboard, string text)
    {
        var extensions = FindLoadedType("Avalonia.Input.Platform.ClipboardExtensions");
        var candidates = extensions?.GetMethods(BindingFlags.Public | BindingFlags.Static) ?? Array.Empty<MethodInfo>();
        foreach (var candidate in candidates)
        {
            var parameters = candidate.GetParameters();
            if (candidate.Name != "SetTextAsync" || parameters.Length != 2 || parameters[1].ParameterType != typeof(string))
            {
                continue;
            }
            if (!parameters[0].ParameterType.IsInstanceOfType(clipboard))
            {
                continue;
            }
            return candidate.Invoke(null, new object[] { clipboard, text });
        }
        return null;
    }

    private static MethodInfo? Method(Type type, string name, params Type[] parameters)
    {
        var method = type.GetMethod(name, parameters);
        if (method != null)
        {
            return method;
        }
        foreach (var contract in type.GetInterfaces())
        {
            method = contract.GetMethod(name, parameters);
            if (method != null)
            {
                return method;
            }
        }
        return null;
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName, throwOnError: false);
            if (type != null)
            {
                return type;
            }
        }
        return null;
    }
}


