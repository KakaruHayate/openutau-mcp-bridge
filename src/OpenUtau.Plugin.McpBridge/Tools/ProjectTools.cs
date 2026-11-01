using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Plugin.McpBridge.Tools;

/// <summary>
/// Project lifecycle: new, open, save, save-as. These change what the session addresses,
/// so the session id rotates and every id handed out earlier stops being valid.
/// </summary>
internal static class ProjectTools
{
    public static object Info(JsonElement? args)
    {
        var project = ReadTools.Project();
        return new JsonObject
        {
            ["sessionId"] = Session.Id,
            ["revision"] = Session.Revision,
            ["name"] = project.name,
            ["path"] = project.FilePath ?? string.Empty,
            ["dirty"] = !DocManager.Inst.ChangesSaved,
            ["lengthTick"] = project.EndTick,
            ["trackCount"] = project.tracks.Count,
            ["partCount"] = project.parts.Count,
        };
    }

    public static object New(JsonElement? args)
    {
        RequireNoUnsavedWork(args);
        var project = Ustx.Create();
        DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
        Session.Rotate();
        return new JsonObject
        {
            ["sessionId"] = Session.Id,
            ["revision"] = Session.Revision,
            ["name"] = project.name,
            ["path"] = string.Empty,
        };
    }

    public static object Open(JsonElement? args)
    {
        RequireNoUnsavedWork(args);
        var path = Args.ReqStr(args, "path");
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new BridgeException("invalid_request", $"File not found: {full}");
        }

        UProject? project;
        try
        {
            project = Formats.ReadProject(new[] { full });
        }
        catch (Exception e)
        {
            throw new BridgeException("invalid_request", $"OpenUtau could not read that project: {e.Message}");
        }
        if (project == null)
        {
            throw new BridgeException("invalid_request", "OpenUtau did not recognise the file as a project.");
        }

        project.FilePath = full;
        DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
        Session.Rotate();
        return new JsonObject
        {
            ["sessionId"] = Session.Id,
            ["revision"] = Session.Revision,
            ["name"] = project.name,
            ["path"] = full,
        };
    }

    public static object Save(JsonElement? args)
    {
        var project = ReadTools.Project();
        var path = project.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            throw new BridgeException("invalid_request", "This project has no path yet; use openutau_project_save_as.");
        }
        SaveAtomically(project, path);
        return Saved(project, path);
    }

    public static object SaveAs(JsonElement? args)
    {
        var project = ReadTools.Project();
        var path = Path.GetFullPath(Args.ReqStr(args, "path"));
        var overwrite = Args.Bool(args, "overwrite", false);
        if (File.Exists(path) && !overwrite)
        {
            throw new BridgeException("overwrite_refused", $"'{path}' already exists; pass overwrite: true to replace it.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SaveAtomically(project, path);
        project.FilePath = path;
        return Saved(project, path);
    }

    public static object AddPart(JsonElement? args)
    {
        var project = ReadTools.Project();
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);

        var trackNo = Args.IntOrNull(args, "trackNo") ?? 0;
        if (trackNo < 0 || trackNo >= project.tracks.Count)
        {
            throw new BridgeException("invalid_request", $"No track {trackNo}.");
        }
        var position = Args.Int(args, "position", 0);
        var duration = Args.Int(args, "duration", 1920);
        if (position < 0 || duration <= 0)
        {
            throw new BridgeException("invalid_request", "position must be >= 0 and duration > 0.");
        }
        var name = Args.Str(args, "name") ?? $"Part {project.parts.Count + 1}";

        var part = new UVoicePart
        {
            name = name,
            trackNo = trackNo,
            position = position,
            duration = duration,
        };

        if (dryRun)
        {
            return new JsonObject
            {
                ["dryRun"] = true,
                ["name"] = name,
                ["trackNo"] = trackNo,
                ["position"] = position,
                ["duration"] = duration,
            };
        }

        DocManager.Inst.StartUndoGroup();
        try
        {
            DocManager.Inst.ExecuteCmd(new AddPartCommand(project, part));
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        if (!project.parts.Contains(part))
        {
            throw new BridgeException("write_verification_failed", "The part was reported added but is not in the project.");
        }

        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["partId"] = part.Id.ToString(),
            ["name"] = part.name,
            ["trackNo"] = part.trackNo,
            ["position"] = part.position,
            ["duration"] = part.Duration,
        };
    }

    public static object RemovePart(JsonElement? args)
    {
        var project = ReadTools.Project();
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);
        var part = ReadTools.FindPart(project, Args.ReqStr(args, "partId"));

        if (dryRun)
        {
            return new JsonObject
            {
                ["dryRun"] = true,
                ["partId"] = part.Id.ToString(),
                ["name"] = part.name,
                ["noteCount"] = part.notes.Count,
            };
        }

        DocManager.Inst.StartUndoGroup();
        try
        {
            DocManager.Inst.ExecuteCmd(new RemovePartCommand(project, part));
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        if (project.parts.Contains(part))
        {
            throw new BridgeException("write_verification_failed", "The part is still in the project after removal.");
        }

        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["partId"] = part.Id.ToString(),
            ["removed"] = true,
        };
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

    // ---- helpers ---------------------------------------------------------------

    private static void SaveAtomically(UProject project, string path)
    {
        var temporary = path + ".tmp-" + Environment.ProcessId;
        try
        {
            // The host's save command is what records the save point in the undo history;
            // point it at a sibling temp file, then publish with a rename.
            DocManager.Inst.ExecuteCmd(new SaveProjectNotification(temporary));
            if (!File.Exists(temporary))
            {
                throw new BridgeException("write_verification_failed", "OpenUtau reported a save but wrote no file.");
            }
            File.Move(temporary, path, overwrite: true);
            // Ustx.Save stamps Project.FilePath with whatever path it was given.
            project.FilePath = path;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch { /* best effort */ }
            }
        }
    }

    private static JsonObject Saved(UProject project, string path) => new()
    {
        ["sessionId"] = Session.Id,
        ["revision"] = Session.Revision,
        ["path"] = path,
        ["dirty"] = !DocManager.Inst.ChangesSaved,
        ["name"] = project.name,
    };

    private static void RequireNoUnsavedWork(JsonElement? args)
    {
        if (DocManager.Inst.ChangesSaved)
        {
            return;
        }
        if (Args.Bool(args, "discardChanges", false))
        {
            return;
        }
        throw new BridgeException("unsaved_changes",
            "The current project has unsaved changes; pass discardChanges: true to throw them away.");
    }
}


