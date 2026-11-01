using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>
/// Owns the loopback HTTP listener and the bridge lifetime. Started from
/// <see cref="BridgeHost.Initialize"/>; there is no start/stop tool by design.
/// </summary>
internal static class BridgeController
{
    private static readonly object Gate = new();
    private static HttpListener? listener;
    private static Thread? loop;
    private static int port = BridgeInfo.DefaultPort;

    public static bool IsRunning
    {
        get { lock (Gate) { return listener?.IsListening == true; } }
    }

    public static int Port
    {
        get { lock (Gate) { return port; } }
    }

    public static string Endpoint => $"http://127.0.0.1:{Port}/mcp";

    public static void Start()
    {
        lock (Gate)
        {
            if (listener != null)
            {
                return;
            }

            var started = false;
            Exception? lastError = null;
            for (var attempt = 0; attempt < BridgeInfo.MaxPortAttempts && !started; attempt++)
            {
                var candidate = BridgeInfo.DefaultPort + attempt;
                var candidateListener = new HttpListener();
                candidateListener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
                try
                {
                    candidateListener.Start();
                    listener = candidateListener;
                    port = candidate;
                    started = true;
                }
                catch (Exception e) when (e is HttpListenerException or SocketException)
                {
                    lastError = e;
                    try { candidateListener.Close(); } catch { /* already dead */ }
                }
            }

            if (!started)
            {
                StatusFile.WriteFailure("listen", lastError ?? new InvalidOperationException("no port available"));
                return;
            }

            loop = new Thread(Run) { IsBackground = true, Name = "OpenUtau.McpBridge" };
            loop.Start();
            StatusFile.WriteRunning();
        }
    }

    private static void Run()
    {
        var current = listener;
        if (current == null)
        {
            return;
        }

        while (current.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = current.GetContext();
            }
            catch (Exception) when (!current.IsListening)
            {
                return; // listener stopped during shutdown
            }
            catch (Exception e)
            {
                StatusFile.AppendError("accept", e);
                return;
            }

            ThreadPool.QueueUserWorkItem(_ => Handle(context), null);
        }
    }

    private static void Handle(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (!string.Equals(path, "/mcp", StringComparison.Ordinal))
            {
                WriteText(context.Response, 404, "Not found.");
                return;
            }
            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers["Allow"] = "POST";
                WriteText(context.Response, 405, "Use POST for this endpoint.");
                return;
            }
            if (!IsAllowedOrigin(context.Request))
            {
                WriteText(context.Response, 403, "Origin not allowed.");
                return;
            }

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = reader.ReadToEnd();
            }

            var payload = McpRequestHandler.Handle(body);
            if (payload == null)
            {
                context.Response.StatusCode = 202;
                context.Response.ContentLength64 = 0;
                context.Response.Close();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(payload);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }
        catch (Exception e)
        {
            StatusFile.AppendError("handle", e);
            try { context.Response.Abort(); } catch { /* client gone */ }
        }
    }

    /// <summary>
    /// Browsers send Origin; local tools do not. Rejecting foreign origins keeps a
    /// web page from driving the editor through a DNS-rebinding trick.
    /// </summary>
    private static bool IsAllowedOrigin(HttpListenerRequest request)
    {
        var origin = request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }
        return origin.Equals($"http://127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase)
            || origin.Equals($"http://localhost:{Port}", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteText(HttpListenerResponse response, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.Close();
    }
}
