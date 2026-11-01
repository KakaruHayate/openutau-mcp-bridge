using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Plugin.McpBridge.Jobs;

namespace OpenUtau.Plugin.McpBridge.Tools;

/// <summary>
/// Audio export. The host's export helpers swallow their own exceptions and only post a
/// notification, so every export renders into a temporary path, verifies the result, and
/// only then publishes: a failed render must never leave a plausible-looking file.
/// </summary>
internal static class ExportTools
{
    private const int DefaultTimeoutSeconds = 300;
    private const int MaxTimeoutSeconds = 1800;

    public static object Capabilities(JsonElement? args)
    {
        var project = ReadTools.Project();
        var reasons = Preflight(project);
        var parts = project.parts.OfType<UVoicePart>().ToList();
        var lengthTick = project.EndTick;
        var seconds = lengthTick > 0 ? Math.Round(lengthTick / (double)project.resolution * 60.0 /
            Math.Max(1.0, project.tempos.FirstOrDefault()?.bpm ?? 120.0), 2) : 0;

        return new JsonObject
        {
            ["formats"] = new JsonArray("wav"),
            ["modes"] = new JsonArray("mixdown", "stems"),
            ["sampleRate"] = 44100,
            ["channels"] = new JsonObject { ["mixdown"] = 2, ["stems"] = 1 },
            ["canExport"] = reasons.Count == 0,
            ["reasons"] = reasons,
            ["estimatedDurationSec"] = seconds,
            ["partCount"] = parts.Count,
        };
    }

    public static object ExportAudio(JsonElement? args)
    {
        var project = ReadTools.Project();
        var mode = (Args.Str(args, "mode") ?? "mixdown").ToLowerInvariant();
        if (mode is not ("mixdown" or "stems"))
        {
            throw new BridgeException("invalid_request", "'mode' must be mixdown or stems.");
        }

        var path = Path.GetFullPath(Args.ReqStr(args, "path"));
        var overwrite = Args.Bool(args, "overwrite", false);
        var timeout = Math.Clamp(Args.Int(args, "timeoutSec", DefaultTimeoutSeconds), 5, MaxTimeoutSeconds);
        var allowIncomplete = Args.Bool(args, "allowIncomplete", false);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        if (File.Exists(path) && !overwrite)
        {
            throw new BridgeException("overwrite_refused", $"'{path}' already exists; pass overwrite: true to replace it.");
        }

        var job = JobManager.Start("export_audio", (token, self) =>
        {
            var reasons = Preflight(project);
            if (reasons.Count > 0)
            {
                throw new BridgeException("unsupported_feature", string.Join("; ", reasons.Select(r => r!.GetValue<string>())));
            }

            var incomplete = project.parts.OfType<UVoicePart>()
                .SelectMany(p => p.notes)
                .Where(n => n.Error || n.OverlapError)
                .ToList();
            if (incomplete.Count > 0 && !allowIncomplete)
            {
                throw new BridgeException("render_incomplete",
                    $"{incomplete.Count} notes are flagged invalid by OpenUtau (overlap or timing); pass allowIncomplete: true to export anyway.");
            }

            token.ThrowIfCancellationRequested();
            return mode == "stems"
                ? ExportStems(project, path, directory ?? ".", overwrite, token, self)
                : ExportMixdown(project, path, overwrite, token, self);
        });

        return new JsonObject
        {
            ["jobId"] = job.Id,
            ["state"] = job.State,
            ["mode"] = mode,
            ["path"] = path,
        };
    }

    public static object JobGet(JsonElement? args)
    {
        var id = Args.ReqStr(args, "jobId");
        var job = JobManager.Get(id) ?? throw new BridgeException("invalid_request", $"Unknown job '{id}'.");
        return job.ToJson();
    }

    public static object JobList(JsonElement? args)
    {
        var limit = Args.Int(args, "limit", 10);
        var array = new JsonArray();
        foreach (var job in JobManager.List(limit))
        {
            array.Add(job.ToJson());
        }
        return new JsonObject { ["jobs"] = array };
    }

    public static object JobCancel(JsonElement? args)
    {
        var id = Args.ReqStr(args, "jobId");
        var job = JobManager.Get(id) ?? throw new BridgeException("invalid_request", $"Unknown job '{id}'.");
        var cancelled = JobManager.Cancel(id);
        var payload = job.ToJson();
        payload["cancelRequested"] = cancelled;
        return payload;
    }

    // ---- rendering -------------------------------------------------------------

    private static object ExportMixdown(UProject project, string path, bool overwrite, CancellationToken token, Job self)
    {
        var temporary = path + ".part-" + self.Id;
        try
        {
            self.Message = "rendering mixdown";
            WaitFor(PlaybackManager.Inst.RenderMixdown(project, temporary), token);
            RequireAudio(temporary);
            token.ThrowIfCancellationRequested();
            Publish(temporary, path, overwrite);
            var info = new FileInfo(path);
            self.Message = "done";
            return new JsonObject
            {
                ["mode"] = "mixdown",
                ["path"] = path,
                ["bytes"] = info.Length,
            };
        }
        finally
        {
            DeleteQuietly(temporary);
        }
    }

    private static object ExportStems(UProject project, string path, string directory, bool overwrite, CancellationToken token, Job self)
    {
        var baseName = Path.GetFileNameWithoutExtension(path);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "openutau-mcp-" + self.Id);
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryBase = Path.Combine(temporaryDirectory, baseName);

        try
        {
            self.Message = "rendering stems";
            WaitFor(PlaybackManager.Inst.RenderToFiles(project, temporaryBase), token);

            var produced = Directory.GetFiles(temporaryDirectory, "*.wav");
            if (produced.Length == 0)
            {
                throw new BridgeException("render_failed", "OpenUtau produced no stem files.");
            }

            token.ThrowIfCancellationRequested();
            var published = new JsonArray();
            foreach (var file in produced)
            {
                var target = Path.Combine(directory, Path.GetFileName(file));
                if (File.Exists(target) && !overwrite)
                {
                    throw new BridgeException("overwrite_refused", $"'{target}' already exists.");
                }
                RequireAudio(file);
                File.Move(file, target, overwrite: true);
                published.Add(new JsonObject
                {
                    ["path"] = target,
                    ["bytes"] = new FileInfo(target).Length,
                });
            }

            self.Message = "done";
            return new JsonObject
            {
                ["mode"] = "stems",
                ["files"] = published,
            };
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The host's export helpers catch their own errors and post a notification, so the
    /// only reliable completion signal is the task plus a file check afterwards.
    /// </summary>
    private static void WaitFor(Task task, CancellationToken token)
    {
        var completed = Task.WhenAny(task, Task.Delay(Timeout.Infinite, token)).GetAwaiter().GetResult();
        if (completed != task)
        {
            throw new OperationCanceledException(token);
        }
        if (task.IsFaulted)
        {
            throw new BridgeException("render_failed", task.Exception?.GetBaseException().Message ?? "Rendering failed.");
        }
    }

    private static void RequireAudio(string path)
    {
        if (!File.Exists(path))
        {
            throw new BridgeException("render_failed", "OpenUtau reported an export but wrote no file.");
        }
        if (new FileInfo(path).Length == 0)
        {
            throw new BridgeException("render_failed", "The exported file is empty.");
        }
    }

    private static void Publish(string temporary, string target, bool overwrite)
    {
        if (File.Exists(target) && !overwrite)
        {
            throw new BridgeException("overwrite_refused", $"'{target}' already exists.");
        }
        File.Move(temporary, target, overwrite: true);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup must never mask the real failure.
        }
    }

    private static JsonArray Preflight(UProject project)
    {
        var reasons = new JsonArray();
        var parts = project.parts.OfType<UVoicePart>().ToList();
        if (parts.Count == 0)
        {
            reasons.Add("the project has no voice parts");
            return reasons;
        }
        foreach (var trackNo in parts.Select(p => p.trackNo).Distinct())
        {
            var track = trackNo >= 0 && trackNo < project.tracks.Count ? project.tracks[trackNo] : null;
            if (track?.Singer == null || !track.Singer.Found)
            {
                reasons.Add($"track {trackNo} has no singer");
            }
        }
        return reasons;
    }
}


