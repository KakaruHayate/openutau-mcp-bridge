using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Plugin.McpBridge.Tools;

/// <summary>
/// Track creation and removal. Editing an existing track lives in <see cref="MusicTools"/>;
/// these two exist because a project starts with a single track and an agent asked to build
/// anything with more than one voice had no way to add one.
/// </summary>
internal static class TrackTools
{
    public static object AddTrack(JsonElement? args)
    {
        var project = ReadTools.Project();
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);

        var index = Args.IntOrNull(args, "index") ?? project.tracks.Count;
        if (index < 0)
        {
            throw new BridgeException("invalid_request", "index must not be negative.");
        }
        if (index > project.tracks.Count)
        {
            index = project.tracks.Count;
        }

        var name = Args.Str(args, "name");
        var color = Args.Str(args, "color");
        var track = new UTrack(project) { TrackNo = index };
        if (!string.IsNullOrWhiteSpace(name))
        {
            track.TrackName = name!;
        }
        if (!string.IsNullOrWhiteSpace(color))
        {
            track.TrackColor = color!;
        }

        if (dryRun)
        {
            return new JsonObject
            {
                ["dryRun"] = true,
                ["index"] = index,
                ["name"] = track.TrackName,
                ["color"] = track.TrackColor,
            };
        }

        DocManager.Inst.StartUndoGroup();
        try
        {
            DocManager.Inst.ExecuteCmd(new AddTrackCommand(project, track));
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        if (!project.tracks.Contains(track))
        {
            throw new BridgeException("write_verification_failed", "The track was reported added but is not in the project.");
        }

        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["trackNo"] = track.TrackNo,
            ["name"] = track.TrackName,
            ["color"] = track.TrackColor,
            ["trackCount"] = project.tracks.Count,
        };
    }

    public static object RemoveTrack(JsonElement? args)
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
        if (project.tracks.Count <= 1)
        {
            throw new BridgeException("invalid_request", "A project needs at least one track.");
        }

        var track = project.tracks[trackNo];
        var orphanParts = project.parts.Count(p => p.trackNo == trackNo);

        if (dryRun)
        {
            return new JsonObject
            {
                ["dryRun"] = true,
                ["trackNo"] = trackNo,
                ["name"] = track.TrackName,
                ["partsRemoved"] = orphanParts,
            };
        }

        DocManager.Inst.StartUndoGroup();
        try
        {
            DocManager.Inst.ExecuteCmd(new RemoveTrackCommand(project, track));
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        if (project.tracks.Contains(track) || project.parts.Any(p => p.trackNo == trackNo && trackNo >= project.tracks.Count))
        {
            throw new BridgeException("write_verification_failed", "The track is still in the project after removal.");
        }

        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["trackNo"] = trackNo,
            ["removed"] = true,
            ["partsRemoved"] = orphanParts,
            ["trackCount"] = project.tracks.Count,
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
}
