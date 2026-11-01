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
Bridge   : {{bridgeVersion}} · protocol {{BridgeInfo.Protocol}} · net10.0

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

        var copied = TryCopyToClipboard(text);
        return copied
            ? $"MCP bridge running at {BridgeController.Endpoint} — install prompt copied to clipboard."
            : $"MCP bridge running at {BridgeController.Endpoint} — prompt saved to {path} (clipboard unavailable).";
    }

    /// <summary>
    /// Avalonia is not referenced at compile time: the plugin only needs its clipboard,
    /// and a hard reference would tie this DLL to the host's Avalonia version.
    /// </summary>
    private static bool TryCopyToClipboard(string text)
    {
        try
        {
            var applicationType = FindLoadedType("Avalonia.Application");
            var current = applicationType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var lifetime = applicationType?.GetProperty("ApplicationLifetime")?.GetValue(current);
            var mainWindow = lifetime?.GetType().GetProperty("MainWindow")?.GetValue(lifetime);
            var clipboard = mainWindow?.GetType().GetProperty("Clipboard")?.GetValue(mainWindow);
            var setText = clipboard?.GetType().GetMethod("SetTextAsync", new[] { typeof(string) });
            if (setText == null)
            {
                return false;
            }

            var result = setText.Invoke(clipboard, new object[] { text });
            if (result is Task task)
            {
                return task.Wait(TimeSpan.FromSeconds(3)) && !task.IsFaulted;
            }
            return true;
        }
        catch (Exception e)
        {
            StatusFile.AppendError("clipboard", e);
            return false;
        }
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
