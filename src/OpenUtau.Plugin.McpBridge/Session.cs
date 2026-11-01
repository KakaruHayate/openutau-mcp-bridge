using OpenUtau.Core;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>Process-scoped session facts exposed by <c>openutau_status</c>.</summary>
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
}
