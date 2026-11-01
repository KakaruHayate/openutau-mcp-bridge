using System.Reflection;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>Identity constants for this bridge build.</summary>
internal static class BridgeInfo
{
    /// <summary>Tool-surface contract version. Bump when tools are added or change meaning.</summary>
    public const string Protocol = "3";

    public const string ServerName = "OpenUtau MCP Bridge";

    /// <summary>Default loopback port; the controller walks upward when it is taken.</summary>
    public const int DefaultPort = 43102;

    public const int MaxPortAttempts = 10;

    /// <summary>
    /// The build's own version. The informational string is preferred because it carries a
    /// release's pre-release tag (0.1.0-test.2) that the assembly version drops.
    /// </summary>
    public static string Version
    {
        get
        {
            var informational = typeof(BridgeInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var clean = informational!.Split('+')[0].Trim();
                if (clean.Length > 0)
                {
                    return clean;
                }
            }
            return typeof(BridgeInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    /// <summary>OpenUtau.Core version this build was compiled against.</summary>
    public static string BuiltAgainst
    {
        get
        {
            var metadata = typeof(BridgeInfo).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "OpenUtauBaseline")?.Value;
            var core = typeof(Core.DocManager).Assembly.GetName().Version?.ToString() ?? "unknown";
            return string.IsNullOrEmpty(metadata) || metadata == "unknown" ? core : metadata;
        }
    }
}
