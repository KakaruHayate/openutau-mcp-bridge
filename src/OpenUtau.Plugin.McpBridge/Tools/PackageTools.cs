using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Classic;
using OpenUtau.Core;
using OpenUtau.Plugin.McpBridge.Jobs;

namespace OpenUtau.Plugin.McpBridge.Tools;

/// <summary>
/// OpenUtau's dependency packages: GAME/RMVPE transcribers, vocoders, themes. Only the
/// registry channel is exposed — installing from an arbitrary file or URL would turn the
/// bridge into a general-purpose archive unpacker.
/// </summary>
internal static class PackageTools
{
    public static object List(JsonElement? args)
    {
        var installed = PackageManager.Inst.GetInstalledAsync().GetAwaiter().GetResult();
        var packages = new JsonArray();
        foreach (var package in installed)
        {
            packages.Add(new JsonObject
            {
                ["id"] = package.id,
                ["version"] = package.version,
                ["description"] = package.description,
                ["path"] = PackageManager.Inst.GetInstalledPath(package.id) ?? string.Empty,
            });
        }
        return new JsonObject
        {
            ["count"] = packages.Count,
            ["packages"] = packages,
        };
    }

    public static object Search(JsonElement? args)
    {
        var query = Args.Str(args, "query");
        var job = JobManager.Start("packages_search", (token, self) =>
        {
            var registry = PackageManager.Inst.FetchRegistryAsync().GetAwaiter().GetResult();
            var result = new JsonArray();
            foreach (var software in registry)
            {
                token.ThrowIfCancellationRequested();
                var name = software.LocalizedName();
                if (!string.IsNullOrEmpty(query)
                    && !name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    && !software.id.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var version = software.versions?.OrderByDescending(v => v.version).FirstOrDefault()?.version ?? string.Empty;
                result.Add(new JsonObject
                {
                    ["id"] = software.id,
                    ["name"] = name,
                    ["version"] = version,
                    ["category"] = software.category,
                    ["tags"] = new JsonArray((software.tags ?? Array.Empty<string>()).Select(t => (JsonNode?)t).ToArray()),
                    ["installed"] = PackageManager.Inst.GetInstalledPath(software.id) != null,
                });
            }
            return new JsonObject { ["count"] = result.Count, ["packages"] = result };
        });
        return new JsonObject { ["jobId"] = job.Id, ["state"] = job.State };
    }

    public static object Install(JsonElement? args)
    {
        var id = Args.ReqStr(args, "id");
        var job = JobManager.Start("packages_install", (token, self) =>
        {
            var registry = PackageManager.Inst.FetchRegistryAsync().GetAwaiter().GetResult();
            var software = registry.FirstOrDefault(s => string.Equals(s.id, id, StringComparison.OrdinalIgnoreCase))
                ?? throw new BridgeException("invalid_request", $"'{id}' is not in OpenUtau's package registry.");

            self.Message = $"downloading {software.id}";
            token.ThrowIfCancellationRequested();

            var progress = new Progress<int>(percent => self.Message = $"installing {software.id}: {percent}%");
            PackageManager.Inst.InstallAsync(software, progress).GetAwaiter().GetResult();

            var path = PackageManager.Inst.GetInstalledPath(software.id)
                ?? throw new BridgeException("write_verification_failed",
                    $"Installation of '{software.id}' finished but the package folder is missing.");

            self.Message = "done";
            return new JsonObject
            {
                ["id"] = software.id,
                ["name"] = software.LocalizedName(),
                ["path"] = path,
            };
        });
        return new JsonObject { ["jobId"] = job.Id, ["state"] = job.State, ["id"] = id };
    }

    public static object Uninstall(JsonElement? args)
    {
        var id = Args.ReqStr(args, "id");
        if (!Args.Bool(args, "confirm", false))
        {
            throw new BridgeException("invalid_request", "Uninstalling a package needs confirm: true.");
        }
        if (PackageManager.Inst.GetInstalledPath(id) == null)
        {
            throw new BridgeException("invalid_request", $"'{id}' is not installed.");
        }
        PackageManager.Inst.UninstallAsync(id).GetAwaiter().GetResult();
        return new JsonObject
        {
            ["id"] = id,
            ["uninstalled"] = PackageManager.Inst.GetInstalledPath(id) == null,
        };
    }

    public static object Refresh(JsonElement? args)
    {
        var singers = 0;
        try
        {
            SingerManager.Inst.SearchAllSingers();
            singers = SingerManager.Inst.Singers.Count;
        }
        catch (Exception e)
        {
            StatusFile.AppendError("packages-refresh:singers", e);
        }

        var packages = 0;
        try
        {
            packages = PackageManager.Inst.GetInstalledAsync().GetAwaiter().GetResult().Count;
        }
        catch (Exception e)
        {
            StatusFile.AppendError("packages-refresh:packages", e);
        }

        var resamplers = 0;
        var wavtools = 0;
        try
        {
            resamplers = ToolsManager.Inst.Resamplers.Count;
            wavtools = ToolsManager.Inst.Wavtools.Count;
        }
        catch (Exception e)
        {
            StatusFile.AppendError("packages-refresh:tools", e);
        }

        return new JsonObject
        {
            ["singers"] = singers,
            ["packages"] = packages,
            ["resamplers"] = resamplers,
            ["wavtools"] = wavtools,
        };
    }
}
