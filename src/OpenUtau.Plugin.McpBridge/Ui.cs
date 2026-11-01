using System.Runtime.ExceptionServices;
using OpenUtau.Core;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>
/// Marshals a tool body onto OpenUtau's UI thread. The host owns the document, so every
/// read and write has to happen there; running on the HTTP thread would race the editor.
/// </summary>
internal static class Ui
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static T Run<T>(Func<T> action)
    {
        Action<Action>? post;
        try
        {
            post = DocManager.Inst.PostOnUIThread;
        }
        catch
        {
            post = null;
        }

        // Headless hosts and tests leave PostOnUIThread null; run inline there.
        if (post == null)
        {
            return action();
        }

        T result = default!;
        ExceptionDispatchInfo? failure = null;
        using var done = new ManualResetEventSlim(false);
        post(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                done.Set();
            }
        });

        if (!done.Wait(Timeout))
        {
            throw new BridgeException("user_busy", "OpenUtau's UI thread did not service the request in time.");
        }
        failure?.Throw();
        return result;
    }

    public static void Run(Action action) => Run<object?>(() => { action(); return null; });
}
