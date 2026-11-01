using System.Text.Json.Nodes;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>
/// The advertised tool surface. Tools whose required capability is missing are not
/// registered, so a Core refactor can only shrink the list, never break calls.
/// </summary>
internal static class ToolRegistry
{
    private sealed record ToolDefinition(string Name, string Title, string Description, string[] Requires);

    private static readonly ToolDefinition[] Definitions =
    {
        new(
            "openutau_status",
            "OpenUtau status",
            "Connection and compatibility facts: bridge/app versions, capability probe, current session and revision. Call this first.",
            Array.Empty<string>()),
    };

    public static IReadOnlyList<string> ToolNames { get; } = Definitions.Select(d => d.Name).ToArray();

    private static List<ToolDefinition> Available()
    {
        var probes = CapabilityProbe.Probe();
        return Definitions
            .Where(d => d.Requires.All(c => probes.TryGetValue(c, out var level) && level == "full"))
            .ToList();
    }

    public static JsonArray Describe()
    {
        var array = new JsonArray();
        foreach (var tool in Available())
        {
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["title"] = tool.Title,
                ["description"] = tool.Description,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                    ["additionalProperties"] = false,
                },
            });
        }
        return array;
    }

    public static object Invoke(string name, System.Text.Json.JsonElement? arguments)
    {
        switch (name)
        {
            case "openutau_status":
                return StatusReport.Build();
            default:
                throw new BridgeException("invalid_request", $"Unknown tool '{name}'.");
        }
    }
}
