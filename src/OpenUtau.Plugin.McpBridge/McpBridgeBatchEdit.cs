using System.Collections.Generic;
using OpenUtau.Core;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>
/// The only menu entry this plugin contributes. OpenUtau lists external
/// <see cref="BatchEdit"/> implementations under <c>Batch Edits → External</c>, so the
/// "connect an agent" affordance ships with the plugin instead of the application.
/// </summary>
public sealed class McpBridgeBatchEdit : BatchEdit
{
    /// <summary>
    /// OpenUtau instantiates this type when the piano-roll window initializes; that is
    /// the earliest point a plugin can run code (see <see cref="BridgeHost"/>).
    /// </summary>
    static McpBridgeBatchEdit() => BridgeHost.EnsureStarted();

    public string Name => "MCP Bridge: Copy agent prompt";

    public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager)
    {
        BridgeHost.EnsureStarted();
        var message = AgentPrompt.Deliver();
        docManager.ExecuteCmd(new ProgressBarNotification(0, message));
    }
}
