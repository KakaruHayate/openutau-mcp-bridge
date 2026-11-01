using System.Text.Json;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>Small helpers for reading tool arguments without a schema library.</summary>
internal static class Args
{
    public static JsonElement? Get(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }
        return value.TryGetProperty(name, out var found) ? found : null;
    }

    public static string? Str(JsonElement? args, string name)
    {
        var element = Get(args, name);
        return element is { ValueKind: JsonValueKind.String } ? element.Value.GetString() : null;
    }

    public static string ReqStr(JsonElement? args, string name)
    {
        var value = Str(args, name);
        if (string.IsNullOrEmpty(value))
        {
            throw new BridgeException("invalid_request", $"'{name}' is required and must be a non-empty string.");
        }
        return value;
    }

    public static int Int(JsonElement? args, string name, int fallback)
    {
        var element = Get(args, name);
        if (element is not { ValueKind: JsonValueKind.Number })
        {
            return fallback;
        }
        return element.Value.TryGetInt32(out var value) ? value : (int)element.Value.GetDouble();
    }

    public static int? IntOrNull(JsonElement? args, string name)
    {
        var element = Get(args, name);
        if (element is not { ValueKind: JsonValueKind.Number })
        {
            return null;
        }
        return element.Value.TryGetInt32(out var value) ? value : (int)element.Value.GetDouble();
    }

    public static long Long(JsonElement? args, string name, long fallback)
    {
        var element = Get(args, name);
        if (element is not { ValueKind: JsonValueKind.Number })
        {
            return fallback;
        }
        return element.Value.TryGetInt64(out var value) ? value : (long)element.Value.GetDouble();
    }

    public static bool Bool(JsonElement? args, string name, bool fallback)
    {
        var element = Get(args, name);
        return element is { ValueKind: JsonValueKind.True } ? true
            : element is { ValueKind: JsonValueKind.False } ? false
            : fallback;
    }

    public static (int Start, int End)? IntRange(JsonElement? args, string name)
    {
        var element = Get(args, name);
        if (element is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() != 2)
        {
            return null;
        }
        var first = array[0];
        var second = array[1];
        if (first.ValueKind != JsonValueKind.Number || second.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        return (first.GetInt32(), second.GetInt32());
    }
}
