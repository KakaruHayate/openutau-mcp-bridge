using System.Runtime.CompilerServices;
using System.Text;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>
/// Starts the bridge when OpenUtau first touches this assembly.
/// <para>
/// Known constraint: OpenUtau only instantiates external <see cref="BatchEdit"/>
/// implementations when the piano-roll window initializes (double-clicking a voice
/// part), and its plugin scan does metadata-only reflection. That means the earliest
/// hook available to a plugin is "first instantiation", not "application start".
/// The module initializer is kept as a second trigger in case a future host version
/// executes assembly code during the scan.
/// </para>
/// </summary>
internal static class BridgeHost
{
    private static readonly object Gate = new();
    private static bool started;

    [ModuleInitializer]
    internal static void Initialize() => EnsureStarted();

    /// <summary>Idempotent; safe to call from any thread more than once.</summary>
    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (started)
            {
                return;
            }
            started = true;
        }

        // Write a boot marker before touching anything else: if the plugin loads but
        // the HTTP server never comes up, this file is the only evidence that the
        // host ran our code at all.
        StatusFile.BootMarkerPath = BootMarker.Write("initializing");

        // Never throw: an exception here would make the host's plugin instantiation
        // fail and OpenUtau would silently skip the plugin.
        try
        {
            BridgeController.Start();
            BootMarker.Write("started " + BridgeController.Endpoint);
        }
        catch (Exception e)
        {
            StatusFile.WriteFailure("startup", e);
            BootMarker.Write("failed: " + e.GetType().Name + ": " + e.Message);
        }
    }
}

/// <summary>
/// A tiny append-only file next to the plugin. It exists so that "the plugin did not
/// load" and "the plugin loaded but the server failed" are distinguishable without a
/// debugger.
/// </summary>
internal static class BootMarker
{
    private static readonly object Gate = new();
    private static string? path;

    public static string? Path
    {
        get
        {
            lock (Gate)
            {
                return path ??= ResolvePath();
            }
        }
    }

    public static string? Write(string message)
    {
        try
        {
            var target = Path;
            if (target == null)
            {
                return null;
            }
            File.AppendAllText(
                target,
                $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never take the host down.
        }
        return Path;
    }

    private static string? ResolvePath()
    {
        try
        {
            var dir = StatusFile.Directory;
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "OpenUtau.Plugin.McpBridge.boot.txt");
        }
        catch
        {
            return null;
        }
    }
}
