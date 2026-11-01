using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Core;
using OpenUtau.Api;
using OpenUtau.Classic;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Plugin.McpBridge.Tools;

/// <summary>
/// Installed singers and the renderer/resampler a track uses. Installed-only: the bridge
/// deliberately cannot install voices, it can only bind what the host already found.
/// </summary>
internal static class SingerTools
{
    public static object ListSingers(JsonElement? args)
    {
        var singers = new JsonArray();
        foreach (var pair in SingerManager.Inst.Singers)
        {
            singers.Add(Describe(pair.Key, pair.Value, detailed: false));
        }
        return new JsonObject
        {
            ["count"] = singers.Count,
            ["singers"] = singers,
        };
    }

    public static object DescribeSinger(JsonElement? args)
    {
        var id = Args.ReqStr(args, "singerId");
        var singer = Find(id);
        var payload = Describe(id, singer, detailed: true);
        payload["renderers"] = RendererList(singer);
        try
        {
            payload["defaultRenderer"] = Renderers.GetDefaultRenderer(singer.SingerType);
        }
        catch
        {
            // A singer type the host has no renderer list for (unloaded or unknown).
            payload["defaultRenderer"] = string.Empty;
        }
        payload["resamplers"] = ToolNames(SafeToolNames(manager => manager.Resamplers.Select(r => r.ToString())));
        payload["wavtools"] = ToolNames(SafeToolNames(manager => manager.Wavtools.Select(w => w.ToString())));
        return payload;
    }

    public static object SetTrackSinger(JsonElement? args)
    {
        var (project, trackNo, track) = TrackTarget(args);
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);
        var followDefault = Args.Bool(args, "followDefaultPhonemizer", true);
        var singerId = Args.ReqStr(args, "singerId");
        var singer = Find(singerId);

        if (!singer.Loaded)
        {
            throw new BridgeException("unsupported_feature",
                $"Singer '{singerId}' is installed but not loaded by OpenUtau yet; open the Singers dialog once and retry.");
        }

        // Two things have to follow the singer or the render fails outright: a renderer
        // that supports its type, and a phonemizer that knows its language. The editor
        // asks the user; an agent gets the defaults instead, and both are reported.
        URenderSettings? renderFix = null;
        var supportedRenderers = Renderers.GetSupportedRenderers(singer.SingerType);
        if (supportedRenderers.Length > 0
            && !supportedRenderers.Contains(track.RendererSettings.renderer ?? string.Empty))
        {
            var preferred = supportedRenderers[0];
            try
            {
                var configured = Renderers.GetDefaultRenderer(singer.SingerType);
                if (supportedRenderers.Contains(configured))
                {
                    preferred = configured;
                }
            }
            catch
            {
                // Fall back to the first supported renderer.
            }
            renderFix = new URenderSettings { renderer = preferred };
        }

        PhonemizerFactory? followUp = null;
        if (followDefault && PhonemizerTools.IsDefault(track.Phonemizer)
            && !string.IsNullOrWhiteSpace(singer.DefaultPhonemizer))
        {
            followUp = PhonemizerTools.Resolve(singer.DefaultPhonemizer!);
        }

        if (dryRun)
        {
            return new JsonObject
            {
                ["dryRun"] = true,
                ["trackNo"] = trackNo,
                ["singerId"] = singer.Id,
                ["singerName"] = singer.Name,
                ["singerType"] = singer.SingerType.ToString(),
                ["renderer"] = renderFix?.renderer ?? track.RendererSettings.renderer ?? string.Empty,
                ["phonemizer"] = followUp != null ? PhonemizerTools.FactoryId(followUp) : (track.Phonemizer != null ? PhonemizerTools.PhonemizerId(track.Phonemizer) : string.Empty),
            };
        }

        DocManager.Inst.StartUndoGroup();
        try
        {
            DocManager.Inst.ExecuteCmd(new TrackChangeSingerCommand(project, track, singer));
            if (renderFix != null)
            {
                DocManager.Inst.ExecuteCmd(new TrackChangeRenderSettingCommand(project, track, renderFix));
            }
            if (followUp != null)
            {
                DocManager.Inst.ExecuteCmd(new TrackChangePhonemizerCommand(project, track, followUp.Create()));
            }
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        if (!string.Equals(track.Singer?.Id, singer.Id, StringComparison.Ordinal))
        {
            throw new BridgeException("write_verification_failed", "The track did not keep the requested singer.");
        }

        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["trackNo"] = trackNo,
            ["singerId"] = singer.Id,
            ["singerName"] = singer.Name,
            ["singerType"] = singer.SingerType.ToString(),
            ["renderer"] = track.RendererSettings.renderer ?? string.Empty,
            ["rendererSource"] = renderFix != null ? "singer type" : "unchanged",
            ["phonemizer"] = track.Phonemizer != null ? PhonemizerTools.PhonemizerId(track.Phonemizer) : string.Empty,
            ["phonemizerSource"] = followUp != null ? "singer default" : "unchanged",
        };
    }

    public static object SetTrackRenderer(JsonElement? args)
    {
        var (project, trackNo, track) = TrackTarget(args);
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);

        var singer = track.Singer;
        if (singer == null || !singer.Found)
        {
            throw new BridgeException("invalid_request", "Bind a singer to this track before choosing a renderer.");
        }

        var supported = Renderers.GetSupportedRenderers(singer.SingerType);
        if (supported.Length == 0)
        {
            throw new BridgeException("unsupported_feature", $"OpenUtau has no renderer list for singer type {singer.SingerType}.");
        }
        var renderer = Args.Str(args, "renderer") ?? track.RendererSettings.renderer ?? supported[0];
        if (!supported.Contains(renderer))
        {
            throw new BridgeException("invalid_request",
                $"Renderer '{renderer}' does not support {singer.SingerType}. Supported: {string.Join(", ", supported)}.");
        }

        var settings = new URenderSettings
        {
            renderer = renderer,
            resampler = Args.Str(args, "resampler") ?? track.RendererSettings.resampler,
            wavtool = Args.Str(args, "wavtool") ?? track.RendererSettings.wavtool,
        };

        if (string.Equals(renderer, Renderers.CLASSIC, StringComparison.Ordinal))
        {
            var resamplers = ToolNames(SafeToolNames(manager => manager.Resamplers.Select(r => r.ToString())))!.AsArray();
            var wavtools = ToolNames(SafeToolNames(manager => manager.Wavtools.Select(w => w.ToString())))!.AsArray();
            if (settings.resampler != null && !resamplers.Any(r => r!.GetValue<string>() == settings.resampler))
            {
                throw new BridgeException("invalid_request", $"Unknown resampler '{settings.resampler}'.");
            }
            if (settings.wavtool != null && !wavtools.Any(w => w!.GetValue<string>() == settings.wavtool))
            {
                throw new BridgeException("invalid_request", $"Unknown wavtool '{settings.wavtool}'.");
            }
        }
        else
        {
            settings.resampler = null!;
            settings.wavtool = null!;
        }

        if (dryRun)
        {
            return new JsonObject
            {
                ["dryRun"] = true,
                ["trackNo"] = trackNo,
                ["renderer"] = settings.renderer,
                ["resampler"] = settings.resampler,
                ["wavtool"] = settings.wavtool,
            };
        }

        DocManager.Inst.StartUndoGroup();
        try
        {
            DocManager.Inst.ExecuteCmd(new TrackChangeRenderSettingCommand(project, track, settings));
        }
        catch (Exception e) when (e is KeyNotFoundException or NullReferenceException)
        {
            throw new BridgeException("unsupported_feature",
                $"OpenUtau's renderer registry is not initialised in this session: {e.Message}");
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        if (!string.Equals(track.RendererSettings.renderer, settings.renderer, StringComparison.Ordinal))
        {
            throw new BridgeException("write_verification_failed", "The track did not keep the requested renderer.");
        }

        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["trackNo"] = trackNo,
            ["renderer"] = track.RendererSettings.renderer ?? string.Empty,
            ["resampler"] = track.RendererSettings.resampler ?? string.Empty,
            ["wavtool"] = track.RendererSettings.wavtool ?? string.Empty,
        };
    }

    // ---- helpers ---------------------------------------------------------------

    private static (UProject Project, int TrackNo, UTrack Track) TrackTarget(JsonElement? args)
    {
        var project = ReadTools.Project();
        var trackNo = Args.IntOrNull(args, "trackNo")
            ?? throw new BridgeException("invalid_request", "'trackNo' is required.");
        if (trackNo < 0 || trackNo >= project.tracks.Count)
        {
            throw new BridgeException("invalid_request", $"No track {trackNo}.");
        }
        return (project, trackNo, project.tracks[trackNo]);
    }

    private static USinger Find(string id)
    {
        foreach (var pair in SingerManager.Inst.Singers)
        {
            if (string.Equals(pair.Key, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Value.Id, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Value.Name, id, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }
        throw new BridgeException("invalid_request", $"Unknown singer '{id}'. Use openutau_list_singers.");
    }

    private static JsonObject Describe(string key, USinger singer, bool detailed)
    {
        var payload = new JsonObject
        {
            ["id"] = string.IsNullOrEmpty(singer.Id) ? key : singer.Id,
            ["name"] = singer.Name,
            ["type"] = singer.SingerType.ToString(),
            ["found"] = singer.Found,
            ["loaded"] = singer.Loaded,
        };
        if (!detailed)
        {
            return payload;
        }

        payload["author"] = singer.Author ?? string.Empty;
        payload["version"] = singer.Version ?? string.Empty;
        payload["basePath"] = singer.BasePath ?? string.Empty;
        payload["defaultPhonemizer"] = singer.DefaultPhonemizer ?? string.Empty;
        var errors = new JsonArray();
        if (singer.Errors != null)
        {
            foreach (var error in singer.Errors)
            {
                errors.Add(error);
            }
        }
        payload["errors"] = errors;
        return payload;
    }

    private static JsonArray RendererList(USinger singer)
    {
        var array = new JsonArray();
        foreach (var renderer in Renderers.GetSupportedRenderers(singer.SingerType))
        {
            array.Add(renderer);
        }
        return array;
    }

    /// <summary>
    /// The tools manager scans disk on first use and its lists can be unset in a host that
    /// never rendered anything, so every access is guarded.
    /// </summary>
    private static IEnumerable<string?>? SafeToolNames(Func<ToolsManager, IEnumerable<string?>> select)
    {
        try
        {
            return select(ToolsManager.Inst).ToList();
        }
        catch
        {
            return null;
        }
    }

    private static JsonNode? ToolNames(IEnumerable<string?>? names)
    {
        if (names == null)
        {
            return null;
        }
        var array = new JsonArray();
        foreach (var name in names)
        {
            if (!string.IsNullOrEmpty(name))
            {
                array.Add(name);
            }
        }
        return array;
    }

    private static void CheckRevision(JsonElement? args)
    {
        var expected = Args.Long(args, "expectedRevision", -1);
        if (expected < 0)
        {
            throw new BridgeException("invalid_request", "'expectedRevision' is required; read openutau_status or a snapshot first.");
        }
        var current = Session.Revision;
        if (expected != current)
        {
            throw new BridgeException("revision_conflict",
                $"expectedRevision {expected} does not match the document revision {current}. Re-read, then retry.");
        }
    }
}





