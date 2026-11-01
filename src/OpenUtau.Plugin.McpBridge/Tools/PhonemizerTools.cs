using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Plugin.McpBridge.Tools;

/// <summary>
/// Phonemizers turn lyrics into phonemes. Without this, a new track keeps the default
/// phonemizer, which passes the lyric through verbatim: a Chinese lyric then reaches a
/// DiffSinger acoustic model as the character itself and the render fails with
/// "phoneme not supported by acoustic model".
/// </summary>
internal static class PhonemizerTools
{
    private static readonly object Gate = new();

    /// <summary>
    /// OpenUtau fills this registry while scanning plugins at startup. A host that has not
    /// reached that point (or a harness) would otherwise report zero phonemizers, so the
    /// list is rebuilt from the already-loaded assemblies on demand.
    /// </summary>
    internal static void EnsureLoaded()
    {
        if ((PhonemizerFactory.GetAll()?.Length ?? 0) > 0)
        {
            return;
        }
        lock (Gate)
        {
            if ((PhonemizerFactory.GetAll()?.Length ?? 0) > 0)
            {
                return;
            }
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).Select(t => t!).ToArray();
                }
                catch
                {
                    continue;
                }
                foreach (var type in types)
                {
                    if (type.IsAbstract || !typeof(Phonemizer).IsAssignableFrom(type)
                        || type.GetCustomAttribute<PhonemizerAttribute>() == null)
                    {
                        continue;
                    }
                    try
                    {
                        PhonemizerFactory.Get(type);
                    }
                    catch
                    {
                        // A broken plugin must not break the listing.
                    }
                }
            }
            PhonemizerFactory.BuildList();
        }
    }

    public static object List(JsonElement? args)
    {
        EnsureLoaded();
        var language = Args.Str(args, "language");
        var list = new JsonArray();
        foreach (var factory in PhonemizerFactory.GetAll() ?? Array.Empty<PhonemizerFactory>())
        {
            if (!string.IsNullOrWhiteSpace(language)
                && !string.Equals(factory.language, language, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            list.Add(new JsonObject
            {
                ["id"] = factory.Id,
                ["tag"] = factory.tag,
                ["name"] = factory.name,
                ["language"] = factory.language ?? string.Empty,
                ["author"] = factory.author ?? string.Empty,
            });
        }
        return new JsonObject
        {
            ["count"] = list.Count,
            ["phonemizers"] = list,
        };
    }

    public static object SetTrackPhonemizer(JsonElement? args)
    {
        var project = ReadTools.Project();
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);
        var trackNo = Args.IntOrNull(args, "trackNo")
            ?? throw new BridgeException("invalid_request", "'trackNo' is required.");
        if (trackNo < 0 || trackNo >= project.tracks.Count)
        {
            throw new BridgeException("invalid_request", $"No track {trackNo}.");
        }
        var track = project.tracks[trackNo];
        var requested = Args.ReqStr(args, "phonemizer");
        var factory = Resolve(requested)
            ?? throw new BridgeException("invalid_request",
                $"Unknown phonemizer '{requested}'. Use openutau_list_phonemizers.");
        var instance = factory.Create();

        if (dryRun)
        {
            return new JsonObject
            {
                ["dryRun"] = true,
                ["trackNo"] = trackNo,
                ["phonemizer"] = factory.Id,
                ["tag"] = factory.tag,
                ["name"] = factory.name,
            };
        }

        DocManager.Inst.StartUndoGroup();
        try
        {
            DocManager.Inst.ExecuteCmd(new TrackChangePhonemizerCommand(project, track, instance));
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        if (!string.Equals(track.Phonemizer.Id, factory.Id, StringComparison.Ordinal))
        {
            throw new BridgeException("write_verification_failed", "The track did not keep the requested phonemizer.");
        }

        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["trackNo"] = trackNo,
            ["phonemizer"] = track.Phonemizer.Id,
            ["tag"] = track.Phonemizer.Tag,
            ["name"] = track.Phonemizer.Name,
            ["language"] = track.Phonemizer.Language ?? string.Empty,
        };
    }

    /// <summary>Id first, then tag, then display name; all case-insensitive.</summary>
    internal static PhonemizerFactory? Resolve(string key)
    {
        EnsureLoaded();
        var all = PhonemizerFactory.GetAll() ?? Array.Empty<PhonemizerFactory>();
        return all.FirstOrDefault(f => string.Equals(f.Id, key, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(f => string.Equals(f.tag, key, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(f => string.Equals(f.name, key, StringComparison.OrdinalIgnoreCase))
            ?? PhonemizerFactory.Get(key);
    }

    /// <summary>
    /// True when the track is still on the pass-through phonemizer, i.e. nothing has
    /// decided how lyrics become phonemes yet.
    /// </summary>
    internal static bool IsDefault(Phonemizer phonemizer) =>
        phonemizer == null || string.Equals(phonemizer.Id, typeof(DefaultPhonemizer).FullName, StringComparison.Ordinal);

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
