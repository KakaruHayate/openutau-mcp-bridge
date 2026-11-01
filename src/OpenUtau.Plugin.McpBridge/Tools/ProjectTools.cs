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

