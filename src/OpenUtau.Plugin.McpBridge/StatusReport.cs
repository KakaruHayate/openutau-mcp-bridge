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

        var missing = probes.Where(p => p.Value != "full").Select(p => p.Key).ToList();
        var reasons = new JsonArray();
        foreach (var capability in missing)
        {
            reasons.Add($"{capability} is not available on this OpenUtau build; its tools are hidden.");
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
                ["ok"] = missing.Count == 0,
                ["level"] = missing.Count == 0 ? "full" : (probes.TryGetValue("notes", out var notesLevel) && notesLevel == "full" ? "partial" : "unsupported"),
                ["reasons"] = reasons,
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

