using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>Minimal stateless MCP request handling over JSON-RPC 2.0.</summary>
internal static class McpRequestHandler
{
    public const string ProtocolVersion = "2025-06-18";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>Returns the response body, or null when the message is a notification.</summary>
    public static string? Handle(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException e)
        {
            return Error(null, -32700, e.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error(null, -32600, "Request must be an object.");
            }

            JsonNode? id = root.TryGetProperty("id", out var idElement) ? JsonNode.Parse(idElement.GetRawText()) : null;
            var hasId = root.TryGetProperty("id", out _);
            var method = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
                ? methodElement.GetString()
                : null;

            switch (method)
            {
                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;
                case "initialize":
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "openutau-mcp-bridge",
                            ["version"] = BridgeInfo.Version,
                        },
                        ["instructions"] =
                            "OpenUtau's live project editor. Call openutau_status first; if compatibility.ok is false, " +
                            "stop and report compatibility.reasons instead of trying other tools.",
                    });
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = ToolRegistry.Describe() });
                case "tools/call":
                    return CallTool(id, root);
                default:
                    return hasId ? Error(id, -32601, $"Method not found: {method}") : null;
            }
        }
    }

    private static string? CallTool(JsonNode? id, JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            return Error(id, -32602, "tools/call requires params.name.");
        }

        var name = nameElement.GetString() ?? string.Empty;
        JsonElement? arguments = parameters.TryGetProperty("arguments", out var argsElement) ? argsElement : null;

        try
        {
            var value = ToolRegistry.Invoke(name, arguments);
            return Result(id, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = JsonSerializer.Serialize(value, JsonOptions),
                    },
                },
                ["isError"] = false,
            });
        }
        catch (BridgeException e)
        {
            return Result(id, ToolError(e.Code, e.Message));
        }
        catch (Exception e)
        {
            // Keep the caller's view short, but leave the full failure next to the DLL:
            // a tool that throws is a bug report waiting for a stack trace.
            StatusFile.WriteFailure($"tool:{name}", e);
            return Result(id, ToolError("internal_error", e.Message));
        }
    }

    private static JsonObject ToolError(string code, string message) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = JsonSerializer.Serialize(new JsonObject { ["error"] = code, ["message"] = message }, JsonOptions),
            },
        },
        ["isError"] = true,
    };

    private static string Result(JsonNode? id, JsonNode result) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    }.ToJsonString(JsonOptions);

    private static string Error(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    }.ToJsonString(JsonOptions);
}

internal sealed class BridgeException : Exception
{
    public string Code { get; }

    public BridgeException(string code, string message) : base(message)
    {
        Code = code;
    }
}
