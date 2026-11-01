using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Core;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>Builds the <c>openutau_status</c> payload shared by the tool and the status file.</summary>
internal static class StatusReport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static JsonObject Build()
    {
        var app = Assembly.GetEntryAssembly();
        var core = typeof(DocManager).Assembly;

        var project = new JsonObject
        {
            ["sessionId"] = Session.Id,
            ["revision"] = Session.Revision,
            ["dirty"] = Session.Dirty,
            ["path"] = Session.ProjectPath ?? string.Empty,
        };

        var probes = CapabilityProbe.Probe();
        var capabilities = new JsonObject();
        foreach (var pair in probes)
        {
            capabilities[pair.Key] = pair.Value;
        }

        var implemented = new JsonArray();
        foreach (var tool in ToolRegistry.ToolNames)
        {
            implemented.Add(tool);
        }

        return new JsonObject
        {
            ["bridge"] = new JsonObject
            {
                ["version"] = BridgeInfo.Version,
                ["protocol"] = int.Parse(BridgeInfo.Protocol),
                ["builtAgainst"] = BridgeInfo.BuiltAgainst,
                ["target"] = "net10.0",
            },
            ["app"] = new JsonObject
            {
                ["version"] = app?.GetName().Version?.ToString() ?? "unknown",
                ["informational"] = InformationalVersion(app) ?? string.Empty,
                ["core"] = core.GetName().Version?.ToString() ?? "unknown",
                ["runtime"] = Environment.Version.ToString(),
                ["os"] = Environment.OSVersion.Platform.ToString(),
            },
            ["compatibility"] = new JsonObject
            {
                ["ok"] = probes.Values.All(v => v == "full"),
                ["level"] = "partial",
                ["reasons"] = new JsonArray("Bridge build " + BridgeInfo.Version + " is the M0 scaffold: only status is implemented."),
                ["capabilities"] = capabilities,
            },
            ["tools"] = new JsonObject
            {
                ["implemented"] = implemented,
            },
            ["project"] = project,
            ["server"] = new JsonObject
            {
                ["endpoint"] = BridgeController.Endpoint,
                ["port"] = BridgeController.Port,
                ["pid"] = Environment.ProcessId,
                ["startedAt"] = Session.StartedAt,
            },
        };
    }

    public static string ToJson(JsonObject payload) => payload.ToJsonString(JsonOptions);

    private static string? InformationalVersion(Assembly? assembly) =>
        assembly?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
}
