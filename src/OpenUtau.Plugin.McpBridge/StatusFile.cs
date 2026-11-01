using System.Text.Json.Nodes;
using OpenUtau.Core;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>
/// Writes <c>OpenUtau.Plugin.McpBridge.status.json</c> next to the plugin DLL.
/// It exists for the case where the HTTP endpoint is unreachable: an agent (or a
/// user filing an issue) can still read why.
/// </summary>
internal static class StatusFile
{
    public const string FileName = "OpenUtau.Plugin.McpBridge.status.json";

    private static readonly object Gate = new();
    private static readonly List<JsonObject> Errors = new();

    /// <summary>Path of the boot marker file, when one could be created.</summary>
    public static string? BootMarkerPath { get; set; }

    public static string Directory
    {
        get
        {
            try
            {
                return PathManager.Inst.PluginsPath;
            }
            catch
            {
                return AppContext.BaseDirectory;
            }
        }
    }

    public static void WriteRunning() => Write();

    public static void WriteFailure(string stage, Exception error)
    {
        AppendError(stage, error);
        Write();
    }

    public static void AppendError(string stage, Exception error)
    {
        lock (Gate)
        {
            Errors.Add(new JsonObject
            {
                ["stage"] = stage,
                ["type"] = error.GetType().Name,
                ["message"] = error.Message,
                ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            });
            while (Errors.Count > 20)
            {
                Errors.RemoveAt(0);
            }
        }
    }

    private static void Write()
    {
        try
        {
            var payload = StatusReport.Build();
            var array = new JsonArray();
            lock (Gate)
            {
                foreach (var error in Errors)
                {
                    array.Add(error.DeepClone());
                }
            }
            payload["errors"] = array;

            var path = Path.Combine(Directory, FileName);
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path, StatusReport.ToJson(payload));
        }
        catch
        {
            // Diagnostics must never take the host down.
        }
    }
}
