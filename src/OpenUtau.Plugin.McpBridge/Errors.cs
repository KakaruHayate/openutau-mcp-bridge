using System.Text.Json.Nodes;
using OpenUtau.Core;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>
/// Maps host exceptions onto the bridge's stable error codes. In particular, OpenUtau
/// signals "this feature needs a package you do not have" with an exception type that is
/// often re-wrapped, so the ids are collected from the whole chain.
/// </summary>
internal static class Errors
{
    public static BridgeException? Map(Exception error)
    {
        if (error is BridgeException bridge)
        {
            return bridge;
        }

        IReadOnlyList<string> missing;
        try
        {
            missing = MissingPackageException.Collect(error);
        }
        catch
        {
            missing = Array.Empty<string>();
        }

        if (missing.Count > 0)
        {
            var packages = new JsonArray();
            foreach (var id in missing)
            {
                packages.Add(id);
            }
            return new BridgeException(
                "missing_package",
                $"OpenUtau needs the package(s) {string.Join(", ", missing)}. Install them with openutau_packages_install.",
                new JsonObject { ["packages"] = packages });
        }

        return null;
    }
}
