using System.Reflection;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>Identity constants for this bridge build.</summary>
internal static class BridgeInfo
{
    /// <summary>Tool-surface contract version. Bump when tools are added or change meaning.</summary>
    public const string Protocol = "1";

    public const string ServerName = "OpenUtau MCP Bridge";

    /// <summary>Default loopback port; the controller walks upward when it is taken.</summary>
    public const int DefaultPort = 43102;

    public const int MaxPortAttempts = 10;

    public static string Version =>
        typeof(BridgeInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>OpenUtau.Core version this build was compiled against.</summary>
    public static string BuiltAgainst =>
        typeof(Core.DocManager).Assembly.GetName().Version?.ToString() ?? "unknown";
}
