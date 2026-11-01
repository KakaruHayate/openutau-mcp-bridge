using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>
/// Process-scoped facts the tools address: session id, document revision, stable note
/// ids within the session, and the change feed.
/// </summary>
internal static class Session
{
    public static string Id { get; } = Guid.NewGuid().ToString("N");

    public static string StartedAt { get; } = DateTimeOffset.UtcNow.ToString("O");

    /// <summary>
    /// OpenUtau already tracks a monotonic document revision; reuse it instead of
    /// maintaining a second counter that could drift from the host's.
    /// </summary>
    public static long Revision
    {
        get
        {
            try
            {
                return DocManager.Inst.Revision.Value;
            }
            catch
            {
                return -1;
            }
        }
    }

    public static bool Dirty
    {
        get
        {
            try
            {
                var project = DocManager.Inst.Project;
                return project != null && !project.Saved;
            }
            catch
            {
                return false;
            }
        }
    }

    public static string? ProjectPath
    {
        get
        {
            try
            {
                return DocManager.Inst.Project?.FilePath;
            }
            catch
            {
                return null;
            }
        }
    }

    // ---- notes -----------------------------------------------------------------

    private static readonly Dictionary<UNote, string> NoteIds = new(ReferenceComparer<UNote>.Instance);
    private static readonly Dictionary<string, UNote> NotesById = new(StringComparer.Ordinal);
    private static long nextNoteId = 1;

    /// <summary>Stable id for a note inside this session; the same object keeps its id.</summary>
    public static string IdOf(UNote note)
    {
        if (NoteIds.TryGetValue(note, out var id))
        {
            return id;
        }
        id = "n" + nextNoteId++;
        NoteIds[note] = id;
        NotesById[id] = note;
        return id;
    }

    public static UNote? NoteById(string id) => NotesById.TryGetValue(id, out var note) ? note : null;

    /// <summary>Drops ids for notes that are no longer part of the given parts.</summary>
    public static void Prune(IEnumerable<UPart> parts)
    {
        var live = new HashSet<UNote>(ReferenceComparer<UNote>.Instance);
        foreach (var part in parts)
        {
            if (part is UVoicePart voicePart)
            {
                foreach (var note in voicePart.notes)
                {
                    live.Add(note);
                }
            }
        }
        if (live.Count == 0)
        {
            return;
        }
        var stale = NoteIds.Keys.Where(n => !live.Contains(n)).ToList();
        foreach (var note in stale)
        {
            NotesById.Remove(NoteIds[note]);
            NoteIds.Remove(note);
        }
    }

    public static void ResetIds()
    {
        NoteIds.Clear();
        NotesById.Clear();
    }
}

internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
{
    public static readonly ReferenceComparer<T> Instance = new();

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
