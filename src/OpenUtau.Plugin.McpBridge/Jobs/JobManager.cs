using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenUtau.Core;

namespace OpenUtau.Plugin.McpBridge.Jobs;

/// <summary>
/// Background work with a handle: export, and anything else that must not block the UI
/// thread. A job reports its fate; its product is read from wherever it was written.
/// </summary>
internal sealed class Job
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public string State { get; set; } = "queued";
    public string Message { get; set; } = string.Empty;
    public object? Result { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();

    public bool IsFinished => State is "succeeded" or "failed" or "cancelled";

    public JsonObject ToJson() => new()
    {
        ["jobId"] = Id,
        ["kind"] = Kind,
        ["state"] = State,
        ["message"] = Message,
        ["startedAt"] = StartedAt.ToString("O"),
        ["finishedAt"] = FinishedAt?.ToString("O"),
        ["result"] = Result == null ? null : System.Text.Json.JsonSerializer.SerializeToNode(Result),
        ["error"] = ErrorCode == null ? null : new JsonObject { ["code"] = ErrorCode, ["message"] = ErrorMessage },
    };
}

internal static class JobManager
{
    private const int KeepFinished = 32;

    private static readonly ConcurrentDictionary<string, Job> Jobs = new();
    private static readonly ConcurrentQueue<string> Order = new();

    public static Job Start(string kind, Func<CancellationToken, Job, object?> work)
    {
        var job = new Job
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            StartedAt = DateTimeOffset.UtcNow,
        };
        Jobs[job.Id] = job;
        Order.Enqueue(job.Id);
        Trim();

        _ = Task.Run(() =>
        {
            if (job.Cancellation.IsCancellationRequested)
            {
                Finish(job, "cancelled", errorCode: "job_cancelled", errorMessage: "Cancelled before it started.");
                return;
            }
            job.State = "running";
            try
            {
                var result = work(job.Cancellation.Token, job);
                if (job.Cancellation.IsCancellationRequested)
                {
                    Finish(job, "cancelled", errorCode: "job_cancelled", errorMessage: "Cancelled while running.");
                    return;
                }
                job.Result = result;
                Finish(job, "succeeded");
            }
            catch (OperationCanceledException)
            {
                Finish(job, "cancelled", errorCode: "job_cancelled", errorMessage: "Cancelled.");
            }
            catch (BridgeException e)
            {
                Finish(job, "failed", e.Code, e.Message);
            }
            catch (Exception e)
            {
                var mapped = Errors.Map(e);
                if (mapped != null)
                {
                    Finish(job, "failed", mapped.Code, mapped.Message);
                    return;
                }
                StatusFile.WriteFailure($"job:{job.Kind}", e);
                Finish(job, "failed", "internal_error", e.Message);
            }
        });

        return job;
    }

    public static Job? Get(string id) => Jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Most recent jobs first.</summary>
    public static IReadOnlyList<Job> List(int limit)
    {
        var ids = Order.Reverse().Take(Math.Clamp(limit, 1, 100));
        var jobs = new List<Job>();
        foreach (var id in ids)
        {
            if (Jobs.TryGetValue(id, out var job))
            {
                jobs.Add(job);
            }
        }
        return jobs;
    }

    public static bool Cancel(string id)
    {
        if (!Jobs.TryGetValue(id, out var job) || job.IsFinished)
        {
            return false;
        }
        job.Cancellation.Cancel();
        return true;
    }

    private static void Finish(Job job, string state, string? errorCode = null, string? errorMessage = null)
    {
        job.State = state;
        job.ErrorCode = errorCode;
        job.ErrorMessage = errorMessage;
        job.FinishedAt = DateTimeOffset.UtcNow;
    }

    private static void Trim()
    {
        while (Order.Count > KeepFinished * 2)
        {
            if (Order.TryDequeue(out var oldest))
            {
                Jobs.TryRemove(oldest, out _);
            }
        }
    }
}

