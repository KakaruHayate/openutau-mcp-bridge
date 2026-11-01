using System.Reflection;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>
/// Probes the running OpenUtau.Core for the members this bridge depends on, without
/// taking hard references to volatile types. The result decides which tools are
/// advertised, so an upstream refactor degrades the surface instead of crashing it.
/// </summary>
internal static class CapabilityProbe
{
    private static readonly Assembly Core = typeof(Core.DocManager).Assembly;

    public static IReadOnlyDictionary<string, string> Probe()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        result["notes"] = Has("OpenUtau.Core.Ustx.UNote") && Has("OpenUtau.Core.Ustx.UVoicePart") ? "full" : "missing";
        result["pitch_points"] = Has("OpenUtau.Core.Ustx.UProject") && Has("OpenUtau.Core.Ustx.UNote") ? "full" : "missing";
        result["vibrato"] = Has("OpenUtau.Core.Ustx.UVibrato") ? "full" : "missing";
        result["phoneme_override"] = Has("OpenUtau.Core.Ustx.UPhonemeOverride") ? "full" : "missing";
        result["expressions"] = Has("OpenUtau.Core.Ustx.UExpressionDescriptor") ? "full" : "missing";
        result["curves"] = Has("OpenUtau.Core.Ustx.UCurve") ? "full" : "missing";
        result["part_geometry"] = Has("OpenUtau.Core.Ustx.UVoicePart") ? "full" : "missing";
        result["track_props"] = Has("OpenUtau.Core.Ustx.UTrack") ? "full" : "missing";
        result["history"] = HasMethod(typeof(Core.DocManager), "StartUndoGroup") && HasMethod(typeof(Core.DocManager), "Undo") ? "full" : "missing";
        result["project_lifecycle"] = Has("OpenUtau.Core.LoadProjectNotification") && Has("OpenUtau.Core.SaveProjectNotification") ? "full" : "missing";
        result["singer_registry"] = Has("OpenUtau.Core.SingerManager") ? "full" : "missing";
        result["export_audio"] = HasMethodByName("OpenUtau.Core.PlaybackManager", "RenderMixdown") && HasMethodByName("OpenUtau.Core.PlaybackManager", "RenderToFiles") ? "full" : "missing";
        result["packages"] = HasMethodByName("OpenUtau.Core.PackageManager", "InstallAsync") ? "full" : "missing";
        return result;
    }

    private static bool Has(string typeName) => Core.GetType(typeName) != null;

    private static bool HasMethod(Type type, string name) => type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static) != null;

    private static bool HasMethodByName(string typeName, string methodName)
    {
        var type = Core.GetType(typeName);
        return type != null && type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static) != null;
    }
}
