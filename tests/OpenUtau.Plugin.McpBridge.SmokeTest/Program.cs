using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Plugin.McpBridge;

// OpenUtau's own dependencies live in a subfolder (see the csproj); resolve them from
// there so the harness never has to shadow the SDK's runtime.
var openUtauLibs = Path.Combine(AppContext.BaseDirectory, "openutau");
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var candidate = Path.Combine(openUtauLibs, name.Name + ".dll");
    return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
};

var failures = new List<string>();

// The host executes document commands on one thread and treats everything else as
// "off the UI thread". Stand up that thread here so the bridge's real marshalling path
// (PostOnUIThread) is exercised instead of bypassed.
using var dispatcher = new Dispatcher();
var project = BuildProject();
InstallProject(project, dispatcher);

// Instantiating the BatchEdit is exactly what OpenUtau's piano roll does; it is also
// what starts the bridge.
_ = new McpBridgeBatchEdit();

var endpoint = BridgeController.Endpoint;
Console.WriteLine($"endpoint: {endpoint}");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

async Task<JsonNode> CallAsync(string method, JsonObject? parameters = null)
{
    var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method };
    if (parameters != null)
    {
        request["params"] = parameters;
    }
    var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
    var response = await http.PostAsync(endpoint, content);
    var body = await response.Content.ReadAsStringAsync();
    if ((int)response.StatusCode != 200)
    {
        throw new Exception($"{method}: HTTP {(int)response.StatusCode} {body}");
    }
    return JsonNode.Parse(body)!;
}

async Task<JsonNode> ToolAsync(string name, JsonObject? arguments = null)
{
    var envelope = await CallAsync("tools/call", new JsonObject
    {
        ["name"] = name,
        ["arguments"] = arguments ?? new JsonObject(),
    });
    var result = envelope["result"]!;
    var text = result["content"]![0]!["text"]!.GetValue<string>();
    var payload = JsonNode.Parse(text)!;
    if (result["isError"]?.GetValue<bool>() == true)
    {
        payload["__isError"] = true;
    }
    return payload;
}

async Task<JsonNode> WaitForJob(string jobId, int seconds = 20)
{
    for (var i = 0; i < seconds * 10; i++)
    {
        var job = await ToolAsync("openutau_job_get", new JsonObject { ["jobId"] = jobId });
        var state = job["state"]?.GetValue<string>();
        if (state is "succeeded" or "failed" or "cancelled")
        {
            return job;
        }
        await Task.Delay(100);
    }
    throw new Exception("job did not finish in time");
}

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
        Console.WriteLine("FAIL " + message);
    }
}

// ---- initialize / tools/list ---------------------------------------------------

var init = await CallAsync("initialize", new JsonObject
{
    ["protocolVersion"] = "2025-06-18",
    ["capabilities"] = new JsonObject(),
    ["clientInfo"] = new JsonObject { ["name"] = "smoke", ["version"] = "1" },
});
Check(init["result"]?["protocolVersion"]?.GetValue<string>() == "2025-06-18", "initialize protocol version");

var listed = await CallAsync("tools/list");
var tools = listed["result"]!["tools"]!.AsArray();
var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
Console.WriteLine("tools: " + string.Join(", ", names));
foreach (var expected in new[]
{
    "openutau_status", "openutau_read_snapshot", "openutau_read_changes", "openutau_list_parts",
    "openutau_read_notes", "openutau_read_pitch", "openutau_list_expressions", "openutau_list_curves",
    "openutau_read_curve", "openutau_add_notes", "openutau_edit_notes", "openutau_delete_notes",
    "openutau_undo", "openutau_redo", "openutau_history_state",
    "openutau_set_pitch", "openutau_set_vibrato", "openutau_set_expression",
    "openutau_write_curve", "openutau_edit_part", "openutau_edit_track",
    "openutau_set_phoneme_override",
    "openutau_list_singers", "openutau_describe_singer", "openutau_set_track_singer",
    "openutau_set_track_renderer", "openutau_export_capabilities", "openutau_export_audio",
    "openutau_job_get", "openutau_job_list", "openutau_job_cancel",
    "openutau_packages_list", "openutau_packages_search", "openutau_packages_install",
    "openutau_packages_uninstall", "openutau_packages_refresh",
    "openutau_add_track", "openutau_remove_track",
    "openutau_add_part", "openutau_remove_part",
    "openutau_project_info", "openutau_project_new",
    "openutau_project_open",
    "openutau_project_save", "openutau_project_save_as",
})
{
    Check(names.Contains(expected), $"tools/list contains {expected}");
}
foreach (var tool in tools)
{
    Check(tool!["inputSchema"] != null, $"{tool["name"]} has inputSchema");
}

// ---- status --------------------------------------------------------------------

var status = await ToolAsync("openutau_status");
Check(status["bridge"]?["protocol"]?.GetValue<int>() == 3, "status protocol");
Check(status["project"]?["sessionId"]?.GetValue<string>()?.Length == 32, "status session id");

// ---- snapshot ------------------------------------------------------------------

var snapshot = await ToolAsync("openutau_read_snapshot");
var parts = snapshot["parts"]!.AsArray();
Check(parts.Count == 1, "snapshot has one part");
Check(snapshot["tracks"]!.AsArray().Count == 1, "snapshot has one track");
var partId = parts[0]!["id"]!.GetValue<string>();
Check(partId.Length == 32, "part id is a guid");
var changeSequence = snapshot["changeSequence"]!.GetValue<long>();

var partList = await ToolAsync("openutau_list_parts");
Check(partList["parts"]!.AsArray().Count == 1, "list_parts count");

// ---- notes ---------------------------------------------------------------------

var notes = await ToolAsync("openutau_read_notes", new JsonObject { ["partId"] = partId });
Check(notes["total"]!.GetValue<int>() == 2, "read_notes total");
var noteArray = notes["notes"]!.AsArray();
Check(noteArray[0]!["tone"]!.GetValue<int>() == 60, "first note tone");
Check(noteArray[0]!["toneName"]!.GetValue<string>() == "C4", "first note tone name");
Check(noteArray[0]!["lyric"]!.GetValue<string>() == "a", "first note lyric");

var full = await ToolAsync("openutau_read_notes", new JsonObject { ["partId"] = partId, ["detail"] = "full" });
Check(full["notes"]![0]!["vibrato"] != null, "full detail carries vibrato");
Check(full["notes"]![0]!["pitchPoints"]!.AsArray().Count == 2, "full detail carries pitch points");

var paged = await ToolAsync("openutau_read_notes", new JsonObject { ["partId"] = partId, ["offset"] = 1, ["limit"] = 1 });
Check(paged["notes"]!.AsArray().Count == 1, "read_notes paging");
Check(paged["notes"]![0]!["position"]!.GetValue<int>() == 480, "paged note is the second one");

// ---- pitch ---------------------------------------------------------------------

var noteId = noteArray[0]!["id"]!.GetValue<string>();
var pitch = await ToolAsync("openutau_read_pitch", new JsonObject { ["partId"] = partId, ["noteId"] = noteId });
Check(pitch["points"]!.AsArray().Count == 2, "read_pitch point count");
Check(pitch["snapFirst"]!.GetValue<bool>(), "read_pitch snapFirst");

// ---- curves --------------------------------------------------------------------

var curves = await ToolAsync("openutau_list_curves", new JsonObject { ["partId"] = partId });
Check(curves["curves"]!.AsArray().Count == 1, "list_curves count");
Check(curves["curves"]![0]!["abbr"]!.GetValue<string>() == "dyn", "curve abbr");

var curve = await ToolAsync("openutau_read_curve", new JsonObject { ["partId"] = partId, ["abbr"] = "dyn" });
Check(curve["points"]!.AsArray().Count == 2, "read_curve points");

// ---- expressions ---------------------------------------------------------------

var expressions = await ToolAsync("openutau_list_expressions");
Check(expressions["project"]!.AsArray().Count >= 1, "list_expressions has project entries");

// ---- changes -------------------------------------------------------------------

var changes = await ToolAsync("openutau_read_changes", new JsonObject { ["sinceSequence"] = 0 });
Check(changes["changeSequence"]!.GetValue<long>() >= changeSequence, "read_changes sequence");
Check(changes["requiresSnapshot"]!.GetValue<bool>() == false, "read_changes does not force a snapshot");

var stale = await ToolAsync("openutau_read_changes", new JsonObject { ["sinceSequence"] = 999999 });
Check(stale["requiresSnapshot"]!.GetValue<bool>(), "future sequence forces a snapshot");

// ---- writes --------------------------------------------------------------------

var revision = snapshot["revision"]!.GetValue<long>();

var dryAdd = await ToolAsync("openutau_add_notes", new JsonObject
{
    ["partId"] = partId,
    ["expectedRevision"] = revision,
    ["notes"] = new JsonArray
    {
        new JsonObject { ["position"] = 960, ["duration"] = 240, ["tone"] = 64, ["lyric"] = "u" },
    },
});
Check(dryAdd["dryRun"]?.GetValue<bool>() == true, "add_notes defaults to dryRun");
var afterDry = await ToolAsync("openutau_read_notes", new JsonObject { ["partId"] = partId });
Check(afterDry["total"]!.GetValue<int>() == 2, "dry run did not change the part");

var added = await ToolAsync("openutau_add_notes", new JsonObject
{
    ["partId"] = partId,
    ["expectedRevision"] = revision,
    ["dryRun"] = false,
    ["notes"] = new JsonArray
    {
        new JsonObject { ["position"] = 960, ["duration"] = 240, ["tone"] = 64, ["lyric"] = "u" },
        new JsonObject { ["position"] = 1200, ["duration"] = 240, ["tone"] = 65, ["lyric"] = "e" },
    },
});
Check(added["__isError"] == null, "add_notes committed");
if (added["__isError"] != null)
{
    Console.WriteLine("add_notes error: " + added.ToJsonString());
    return 1;
}
Check(added["added"]!.AsArray().Count == 2, "add_notes returned two ids");

var afterAdd = await ToolAsync("openutau_read_notes", new JsonObject { ["partId"] = partId });
Check(afterAdd["total"]!.GetValue<int>() == 4, "add_notes added two notes");

var revision2 = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>();
Check(revision2 == revision + 1, "one undo group bumps the revision once");

var historyAfterAdd = await ToolAsync("openutau_history_state");
Check(historyAfterAdd["history"]!["undo"]!.GetValue<int>() >= 1, "history records the add group");

var editId = afterAdd["notes"]![3]!["id"]!.GetValue<string>();
var edited = await ToolAsync("openutau_edit_notes", new JsonObject
{
    ["partId"] = partId,
    ["expectedRevision"] = revision2,
    ["dryRun"] = false,
    ["edits"] = new JsonArray
    {
        new JsonObject { ["noteId"] = editId, ["position"] = 1440, ["lyric"] = "o" },
    },
});
Check(edited["edited"]!.GetValue<int>() == 1, "edit_notes applied one edit");

var afterEdit = await ToolAsync("openutau_read_notes", new JsonObject { ["partId"] = partId, ["offset"] = 3, ["limit"] = 1 });
Check(afterEdit["notes"]![0]!["position"]!.GetValue<int>() == 1440, "edit moved the note");
Check(afterEdit["notes"]![0]!["lyric"]!.GetValue<string>() == "o", "edit changed the lyric");

var revision3 = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>();
var deleted = await ToolAsync("openutau_delete_notes", new JsonObject
{
    ["partId"] = partId,
    ["expectedRevision"] = revision3,
    ["dryRun"] = false,
    ["noteIds"] = new JsonArray { editId },
});
Check(deleted["deleted"]!.GetValue<int>() == 1, "delete_notes removed one");

var afterDelete = await ToolAsync("openutau_read_notes", new JsonObject { ["partId"] = partId });
Check(afterDelete["total"]!.GetValue<int>() == 3, "delete reduced the count");

var undone = await ToolAsync("openutau_undo");
Check(undone["__isError"] == null, "undo ran");
var afterUndo = await ToolAsync("openutau_read_notes", new JsonObject { ["partId"] = partId });
Check(afterUndo["total"]!.GetValue<int>() == 4, "undo restored the deleted note");

var conflict = await ToolAsync("openutau_add_notes", new JsonObject
{
    ["partId"] = partId,
    ["expectedRevision"] = revision,
    ["dryRun"] = false,
    ["notes"] = new JsonArray { new JsonObject { ["position"] = 0, ["duration"] = 120, ["tone"] = 60 } },
});
Check(conflict["error"]?.GetValue<string>() == "revision_conflict", "stale revision rejected");

var missingRevision = await ToolAsync("openutau_delete_notes", new JsonObject
{
    ["partId"] = partId,
    ["dryRun"] = false,
    ["noteIds"] = new JsonArray { editId },
});
Check(missingRevision["error"]?.GetValue<string>() == "invalid_request", "missing expectedRevision rejected");

// ---- musical edits -------------------------------------------------------------

var current = await ToolAsync("openutau_read_notes", new JsonObject { ["partId"] = partId, ["detail"] = "full" });
var targetId = current["notes"]![1]!["id"]!.GetValue<string>();
var revMusic = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>();

var pitchSet = await ToolAsync("openutau_set_pitch", new JsonObject
{
    ["partId"] = partId,
    ["noteId"] = targetId,
    ["expectedRevision"] = revMusic,
    ["dryRun"] = false,
    ["points"] = new JsonArray
    {
        new JsonObject { ["xMs"] = 0, ["y"] = 0, ["shape"] = "io" },
        new JsonObject { ["xMs"] = 100, ["y"] = 20, ["shape"] = "l" },
        new JsonObject { ["xMs"] = 240, ["y"] = 0, ["shape"] = "io" },
    },
});
Check(pitchSet["__isError"] == null, "set_pitch committed");
if (pitchSet["__isError"] != null)
{
    Console.WriteLine("set_pitch error: " + pitchSet.ToJsonString());
}
var pitchRead = await ToolAsync("openutau_read_pitch", new JsonObject { ["partId"] = partId, ["noteId"] = targetId });
Check(pitchRead["points"]!.AsArray().Count == 3, "set_pitch wrote three points");
Check(pitchRead["points"]![1]!["y"]!.GetValue<double>() == 20, "second pitch point y");

var pitchReset = await ToolAsync("openutau_set_pitch", new JsonObject
{
    ["partId"] = partId,
    ["noteId"] = targetId,
    ["expectedRevision"] = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>(),
    ["dryRun"] = false,
    ["reset"] = true,
});
Check(pitchReset["__isError"] == null, "set_pitch reset committed");

var revVibrato = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>();
var vibratoSet = await ToolAsync("openutau_set_vibrato", new JsonObject
{
    ["partId"] = partId,
    ["noteId"] = targetId,
    ["expectedRevision"] = revVibrato,
    ["dryRun"] = false,
    ["depth"] = 40,
    ["period"] = 200,
    ["length"] = 50,
});
Check(vibratoSet["__isError"] == null, "set_vibrato committed");
var afterVibrato = await ToolAsync("openutau_read_notes", new JsonObject { ["partId"] = partId, ["detail"] = "full" });
var vibratoNote = afterVibrato["notes"]!.AsArray().First(n => n!["id"]!.GetValue<string>() == targetId)!;
Check(Math.Abs(vibratoNote["vibrato"]!["depth"]!.GetValue<double>() - 40) < 0.01, "vibrato depth applied");
Check(Math.Abs(vibratoNote["vibrato"]!["period"]!.GetValue<double>() - 200) < 0.01, "vibrato period applied");

var revExpression = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>();
var expressionSet = await ToolAsync("openutau_set_expression", new JsonObject
{
    ["partId"] = partId,
    ["noteIds"] = new JsonArray { targetId },
    ["abbr"] = "vel",
    ["value"] = 150,
    ["expectedRevision"] = revExpression,
    ["dryRun"] = false,
});
Check(expressionSet["__isError"] == null, "set_expression committed");
if (expressionSet["__isError"] != null)
{
    Console.WriteLine("set_expression error: " + expressionSet.ToJsonString());
}
var afterExpression = await ToolAsync("openutau_read_notes", new JsonObject { ["partId"] = partId, ["detail"] = "full" });
var expressionNote = afterExpression["notes"]!.AsArray().First(n => n!["id"]!.GetValue<string>() == targetId)!;
Check(expressionNote["expressions"]!.AsArray().Any(e => e!["abbr"]!.GetValue<string>() == "vel"
    && Math.Abs(e["value"]!.GetValue<double>() - 150) < 0.01), "expression applied");

var revCurve = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>();
var curveSet = await ToolAsync("openutau_write_curve", new JsonObject
{
    ["partId"] = partId,
    ["abbr"] = "dyn",
    ["expectedRevision"] = revCurve,
    ["dryRun"] = false,
    ["points"] = new JsonArray
    {
        new JsonObject { ["tick"] = 0, ["value"] = 0 },
        new JsonObject { ["tick"] = 480, ["value"] = 20 },
        new JsonObject { ["tick"] = 960, ["value"] = 0 },
    },
});
Check(curveSet["__isError"] == null, "write_curve committed");
if (curveSet["__isError"] != null)
{
    Console.WriteLine("write_curve error: " + curveSet.ToJsonString());
}
var curveRead = await ToolAsync("openutau_read_curve", new JsonObject { ["partId"] = partId, ["abbr"] = "dyn" });
Check(curveRead["total"]!.GetValue<int>() == 3, "write_curve wrote three points");
Check(curveRead["points"]![1]!["value"]!.GetValue<int>() == 20, "curve value at 480");

// ifMatch: a write must refuse to overwrite content that changed since the read.
var fingerprint = (await ToolAsync("openutau_read_pitch", new JsonObject { ["partId"] = partId, ["noteId"] = targetId }))["fingerprint"]!.GetValue<string>();
Check(fingerprint.Length == 24, "read_pitch returns a fingerprint");

var guarded = await ToolAsync("openutau_set_vibrato", new JsonObject
{
    ["partId"] = partId,
    ["noteId"] = targetId,
    ["expectedRevision"] = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>(),
    ["dryRun"] = false,
    ["ifMatch"] = fingerprint,
    ["depth"] = 30,
});
Check(guarded["__isError"] == null, "fresh ifMatch lets the write through");

var staleWrite = await ToolAsync("openutau_set_vibrato", new JsonObject
{
    ["partId"] = partId,
    ["noteId"] = targetId,
    ["expectedRevision"] = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>(),
    ["dryRun"] = false,
    ["ifMatch"] = fingerprint,
    ["depth"] = 10,
});
Check(staleWrite["error"]?.GetValue<string>() == "stale_write", "stale ifMatch is rejected");

var curveFingerprint = (await ToolAsync("openutau_read_curve", new JsonObject { ["partId"] = partId, ["abbr"] = "dyn" }))["fingerprint"]!.GetValue<string>();
Check(curveFingerprint.Length == 24, "read_curve returns a fingerprint");

var curveGuarded = await ToolAsync("openutau_write_curve", new JsonObject
{
    ["partId"] = partId,
    ["abbr"] = "dyn",
    ["expectedRevision"] = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>(),
    ["dryRun"] = false,
    ["ifMatch"] = curveFingerprint,
    ["points"] = new JsonArray
    {
        new JsonObject { ["tick"] = 0, ["value"] = 0 },
        new JsonObject { ["tick"] = 960, ["value"] = 5 },
    },
});
Check(curveGuarded["__isError"] == null, "fresh curve fingerprint is accepted");

var curveStale = await ToolAsync("openutau_write_curve", new JsonObject
{
    ["partId"] = partId,
    ["abbr"] = "dyn",
    ["expectedRevision"] = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>(),
    ["dryRun"] = false,
    ["ifMatch"] = curveFingerprint,
    ["points"] = new JsonArray { new JsonObject { ["tick"] = 0, ["value"] = 1 } },
});
Check(curveStale["error"]?.GetValue<string>() == "stale_write", "stale curve fingerprint is rejected");

var revPart = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>();
var partEdited = await ToolAsync("openutau_edit_part", new JsonObject
{
    ["partId"] = partId,
    ["expectedRevision"] = revPart,
    ["dryRun"] = false,
    ["name"] = "Verse",
    ["position"] = 240,
    ["duration"] = 2400,
});
Check(partEdited["__isError"] == null, "edit_part committed");
if (partEdited["__isError"] != null)
{
    Console.WriteLine("edit_part error: " + partEdited.ToJsonString());
}
var afterPart = await ToolAsync("openutau_read_snapshot");
var partNow = afterPart["parts"]![0]!;
Check(partNow["name"]!.GetValue<string>() == "Verse", "part renamed");
Check(partNow["position"]!.GetValue<int>() == 240, "part moved");
Check(partNow["duration"]!.GetValue<int>() == 2400, "part resized");

var revTrack = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>();
var trackEdited = await ToolAsync("openutau_edit_track", new JsonObject
{
    ["trackNo"] = 0,
    ["expectedRevision"] = revTrack,
    ["dryRun"] = false,
    ["name"] = "Lead",
    ["color"] = "Red",
    ["volume"] = -3.0,
});
Check(trackEdited["__isError"] == null, "edit_track committed");
var afterTrack = await ToolAsync("openutau_read_snapshot");
var trackNow = afterTrack["tracks"]![0]!;
Check(trackNow["name"]!.GetValue<string>() == "Lead", "track renamed");
Check(Math.Abs(trackNow["volume"]!.GetValue<double>() - (-3.0)) < 0.01, "track volume applied");

var revPhoneme = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>();
var overrideSet = await ToolAsync("openutau_set_phoneme_override", new JsonObject
{
    ["partId"] = partId,
    ["noteId"] = targetId,
    ["index"] = 0,
    ["field"] = "offset",
    ["value"] = 30,
    ["expectedRevision"] = revPhoneme,
    ["dryRun"] = false,
});
Check(overrideSet["__isError"] == null, "set_phoneme_override committed");
if (overrideSet["__isError"] != null)
{
    Console.WriteLine("set_phoneme_override error: " + overrideSet.ToJsonString());
}
var afterOverride = await ToolAsync("openutau_read_notes", new JsonObject { ["partId"] = partId, ["detail"] = "full" });
var overrideNote = afterOverride["notes"]!.AsArray().First(n => n!["id"]!.GetValue<string>() == targetId)!;
Check(overrideNote["phonemeOverrides"]!.AsArray().Any(o => o!["offset"]?.GetValue<int>() == 30), "phoneme offset applied");

var unsupported = await ToolAsync("openutau_set_phoneme_override", new JsonObject
{
    ["partId"] = partId,
    ["noteId"] = targetId,
    ["index"] = 0,
    ["field"] = "preutterDelta",
    ["value"] = 10,
    ["expectedRevision"] = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>(),
    ["dryRun"] = false,
});
Check(unsupported["error"]?.GetValue<string>() == "unsupported_feature", "timing override reports unsupported_feature");
// ---- singers ------------------------------------------------------------------

var fakeSinger = new FakeSinger();
SingerManager.Inst.Singers["smoke-singer"] = fakeSinger;

var singers = await ToolAsync("openutau_list_singers");
Check(singers["singers"]!.AsArray().Any(s => s!["name"]!.GetValue<string>() == "SmokeSinger"), "list_singers shows the test singer");

var described = await ToolAsync("openutau_describe_singer", new JsonObject { ["singerId"] = "SmokeSinger" });
Check(described["__isError"] == null, "describe_singer ran");
if (described["__isError"] != null)
{
    Console.WriteLine("describe_singer error: " + described.ToJsonString());
}
else
{
    Check(described["renderers"]!.AsArray().Count >= 1, "describe_singer lists renderers");
}

// Binding a singer needs a fully loaded voicebank and an initialized renderer registry,
// neither of which exists in a headless harness; the error paths are covered here and
// the happy path is exercised against a real host.
var unknownSinger = await ToolAsync("openutau_set_track_singer", new JsonObject
{
    ["trackNo"] = 0,
    ["singerId"] = "no-such-singer",
    ["expectedRevision"] = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>(),
    ["dryRun"] = false,
});
Check(unknownSinger["error"]?.GetValue<string>() == "invalid_request", "unknown singer rejected");

var noSingerRenderer = await ToolAsync("openutau_set_track_renderer", new JsonObject
{
    ["trackNo"] = 0,
    ["renderer"] = "CLASSIC",
    ["expectedRevision"] = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>(),
    ["dryRun"] = true,
});
Check(noSingerRenderer["error"]?.GetValue<string>() == "invalid_request", "renderer binding needs a singer first");
// ---- parts ---------------------------------------------------------------------

// ---- tracks --------------------------------------------------------------------

var tracksBefore = (await ToolAsync("openutau_read_snapshot"))["tracks"]!.AsArray().Count;
var addedTrack = await ToolAsync("openutau_add_track", new JsonObject
{
    ["name"] = "Harmony",
    ["color"] = "Green",
    ["expectedRevision"] = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>(),
    ["dryRun"] = false,
});
Check(addedTrack["__isError"] == null, "add_track committed");
if (addedTrack["__isError"] != null)
{
    Console.WriteLine("add_track error: " + addedTrack.ToJsonString());
}
var trackAfterAdd = await ToolAsync("openutau_read_snapshot");
Check(trackAfterAdd["tracks"]!.AsArray().Count == tracksBefore + 1, "add_track grew the track list");
var newTrackNo = addedTrack["trackNo"]!.GetValue<int>();
Check(trackAfterAdd["tracks"]![newTrackNo]!["name"]!.GetValue<string>() == "Harmony", "new track carries its name");

var removedTrack = await ToolAsync("openutau_remove_track", new JsonObject
{
    ["trackNo"] = newTrackNo,
    ["expectedRevision"] = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>(),
    ["dryRun"] = false,
});
Check(removedTrack["removed"]?.GetValue<bool>() == true, "remove_track committed");
Check((await ToolAsync("openutau_read_snapshot"))["tracks"]!.AsArray().Count == tracksBefore, "remove_track restored the count");

var lastTrack = await ToolAsync("openutau_remove_track", new JsonObject
{
    ["trackNo"] = 0,
    ["expectedRevision"] = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>(),
    ["dryRun"] = true,
});
Check(lastTrack["error"]?.GetValue<string>() == "invalid_request", "the last track cannot be removed");
var revisionParts = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>();
var partsBefore = (await ToolAsync("openutau_read_snapshot"))["parts"]!.AsArray().Count;
var addedPart = await ToolAsync("openutau_add_part", new JsonObject
{
    ["trackNo"] = 0,
    ["name"] = "Scratch",
    ["duration"] = 960,
    ["expectedRevision"] = revisionParts,
    ["dryRun"] = false,
});
Check(addedPart["__isError"] == null, "add_part committed");
if (addedPart["__isError"] != null)
{
    Console.WriteLine("add_part error: " + addedPart.ToJsonString());
}
var newPartId = addedPart["partId"]!.GetValue<string>();
Check((await ToolAsync("openutau_read_snapshot"))["parts"]!.AsArray().Count == partsBefore + 1, "add_part grew the project");

var partNotes = await ToolAsync("openutau_add_notes", new JsonObject
{
    ["partId"] = newPartId,
    ["expectedRevision"] = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>(),
    ["dryRun"] = false,
    ["notes"] = new JsonArray { new JsonObject { ["position"] = 0, ["duration"] = 480, ["tone"] = 60 } },
});
Check(partNotes["__isError"] == null, "notes can be added to a new part");

var removedPart = await ToolAsync("openutau_remove_part", new JsonObject
{
    ["partId"] = newPartId,
    ["expectedRevision"] = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>(),
    ["dryRun"] = false,
});
Check(removedPart["removed"]?.GetValue<bool>() == true, "remove_part committed");
Check((await ToolAsync("openutau_read_snapshot"))["parts"]!.AsArray().Count == partsBefore, "remove_part restored the count");
// ---- export and jobs -----------------------------------------------------------

var capabilities = await ToolAsync("openutau_export_capabilities");
Check(capabilities["formats"]!.AsArray().Any(f => f!.GetValue<string>() == "wav"), "export capabilities list wav");
Check(capabilities["canExport"]!.GetValue<bool>() == false, "export blocked without a singer");
Check(capabilities["reasons"]!.AsArray().Count >= 1, "export capabilities explain why");

var exportPath = Path.Combine(Path.GetTempPath(), "openutau-mcp-bridge-smoke", "mix.wav");
Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
if (File.Exists(exportPath))
{
    File.Delete(exportPath);
}

var export = await ToolAsync("openutau_export_audio", new JsonObject
{
    ["mode"] = "mixdown",
    ["path"] = exportPath,
});
Check(export["jobId"]?.GetValue<string>()?.Length == 32, "export_audio returned a job id");
var exportJob = await WaitForJob(export["jobId"]!.GetValue<string>());
Check(exportJob["state"]!.GetValue<string>() == "failed", "export without a singer fails the job");
Check(exportJob["error"]!["code"]!.GetValue<string>() == "unsupported_feature", "export failure names the reason");
Check(!File.Exists(exportPath), "failed export published nothing");

var jobs = await ToolAsync("openutau_job_list", new JsonObject { ["limit"] = 5 });
Check(jobs["jobs"]!.AsArray().Count >= 1, "job_list shows the export");
Check(jobs["jobs"]![0]!["jobId"]!.GetValue<string>() == export["jobId"]!.GetValue<string>(), "job_list is newest first");

var cancelFinished = await ToolAsync("openutau_job_cancel", new JsonObject { ["jobId"] = export["jobId"]!.GetValue<string>() });
Check(cancelFinished["cancelRequested"]!.GetValue<bool>() == false, "a finished job cannot be cancelled");

var unknownJob = await ToolAsync("openutau_job_get", new JsonObject { ["jobId"] = "nope" });
Check(unknownJob["error"]?.GetValue<string>() == "invalid_request", "unknown job id rejected");

File.WriteAllText(exportPath, "occupied");
var refusedExport = await ToolAsync("openutau_export_audio", new JsonObject
{
    ["mode"] = "mixdown",
    ["path"] = exportPath,
});
Check(refusedExport["error"]?.GetValue<string>() == "overwrite_refused", "export refuses to overwrite by default");
File.Delete(exportPath);
// ---- agent prompt --------------------------------------------------------------

var prompt = OpenUtau.Plugin.McpBridge.AgentPrompt.Build();
Check(prompt.All(ch => ch < 128), "agent prompt stays ASCII (a mojibake here reaches the user's chat)");
Check(prompt.Contains("Endpoint : http://127.0.0.1:"), "agent prompt carries the live endpoint");
Check(prompt.Contains("openutau_status"), "agent prompt tells the agent to call status first");
// ---- capability filtering ------------------------------------------------------

var healthy = new Dictionary<string, string>
{
    ["notes"] = "full", ["pitch_points"] = "full", ["vibrato"] = "full",
    ["phoneme_override"] = "full", ["expressions"] = "full", ["curves"] = "full",
    ["part_geometry"] = "full", ["track_props"] = "full", ["history"] = "full",
    ["project_lifecycle"] = "full", ["singer_registry"] = "full",
    ["export_audio"] = "full", ["packages"] = "full",
};
var allNames = OpenUtau.Plugin.McpBridge.ToolRegistry.AvailableToolNames(healthy);
Check(allNames.Count == names.Count, "every tool is available when all capabilities probe full");

var brokenCurves = new Dictionary<string, string>(healthy) { ["curves"] = "missing" };
var filtered = OpenUtau.Plugin.McpBridge.ToolRegistry.AvailableToolNames(brokenCurves);
Check(!filtered.Contains("openutau_read_curve"), "curve tools disappear when the capability is missing");
Check(filtered.Contains("openutau_read_notes"), "unrelated tools stay available");
Check(filtered.Count == allNames.Count - 3, "exactly the curve tools are removed");

var noHistory = new Dictionary<string, string>(healthy) { ["history"] = "missing" };
var readOnly = OpenUtau.Plugin.McpBridge.ToolRegistry.AvailableToolNames(noHistory);
Check(!readOnly.Contains("openutau_add_notes"), "writes disappear without history");
Check(readOnly.Contains("openutau_status"), "status never depends on a capability");

var filteredList = OpenUtau.Plugin.McpBridge.ToolRegistry.Describe(brokenCurves);
Check(filteredList.AsArray().All(t => t!["name"]!.GetValue<string>() != "openutau_list_curves"), "tools/list respects the probe");
// ---- packages ------------------------------------------------------------------

var installedPackages = await ToolAsync("openutau_packages_list");
Check(installedPackages["packages"] != null, "packages_list returns an array");
Check(installedPackages["count"]!.GetValue<int>() == installedPackages["packages"]!.AsArray().Count, "packages_list count matches");

var uninstallUnknown = await ToolAsync("openutau_packages_uninstall", new JsonObject { ["id"] = "nope", ["confirm"] = true });
Check(uninstallUnknown["error"]?.GetValue<string>() == "invalid_request", "uninstalling an unknown package is rejected");

var uninstallUnconfirmed = await ToolAsync("openutau_packages_uninstall", new JsonObject { ["id"] = "nope" });
Check(uninstallUnconfirmed["error"]?.GetValue<string>() == "invalid_request", "uninstall needs confirm");

var refreshed = await ToolAsync("openutau_packages_refresh");
Check(refreshed["__isError"] == null, "packages_refresh ran");

// The missing-package mapping is what makes GAME / RMVPE / vocoder gaps self-healing.
var mapped = OpenUtau.Plugin.McpBridge.Errors.Map(new MissingPackageException("game", "rmvpe"));
Check(mapped?.Code == "missing_package", "missing package maps to its own error code");
Check(mapped?.Details?["packages"]?.AsArray().Count == 2, "missing package lists both ids");
// ---- project lifecycle ---------------------------------------------------------

var info = await ToolAsync("openutau_project_info");
var session1 = info["sessionId"]!.GetValue<string>();
Check(info["partCount"]!.GetValue<int>() == 1, "project_info part count");

var savePath = Path.Combine(Path.GetTempPath(), "openutau-mcp-bridge-smoke", "smoke.ustx");
Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
if (File.Exists(savePath))
{
    File.Delete(savePath);
}

var saved = await ToolAsync("openutau_project_save_as", new JsonObject { ["path"] = savePath });
Check(saved["__isError"] == null, "save_as committed");
if (saved["__isError"] != null)
{
    Console.WriteLine("save_as error: " + saved.ToJsonString());
}
Check(File.Exists(savePath), "save_as wrote the file");
Check(saved["path"]?.GetValue<string>() == savePath, "save_as reports the path");

var created = await ToolAsync("openutau_project_new", new JsonObject { ["discardChanges"] = true });
Check(created["__isError"] == null, "project_new ran");
var session2 = created["sessionId"]!.GetValue<string>();
Check(session2 != session1, "session rotates on new");
var afterNew = await ToolAsync("openutau_read_snapshot");
Check(afterNew["parts"]!.AsArray().Count == 0, "new project has no parts");

var reopened = await ToolAsync("openutau_project_open", new JsonObject { ["path"] = savePath, ["discardChanges"] = true });
Check(reopened["__isError"] == null, "project_open ran");
if (reopened["__isError"] != null)
{
    Console.WriteLine("project_open error: " + reopened.ToJsonString());
}
var session3 = reopened["sessionId"]!.GetValue<string>();
Check(session3 != session2, "session rotates on open");
var afterOpen = await ToolAsync("openutau_read_snapshot");
Check(afterOpen["parts"]!.AsArray().Count == 1, "opened project has its part back");
Check(afterOpen["project"]!["name"]!.GetValue<string>() == "Smoke Project", "opened project keeps its name");

var refused = await ToolAsync("openutau_project_save_as", new JsonObject { ["path"] = savePath });
Check(refused["error"]?.GetValue<string>() == "overwrite_refused", "save_as refuses to overwrite");

var savedAgain = await ToolAsync("openutau_project_save");
Check(savedAgain["__isError"] == null, "project_save works after open");

var revision4 = (await ToolAsync("openutau_status"))["project"]!["revision"]!.GetValue<long>();
var partAfterOpen = afterOpen["parts"]![0]!["id"]!.GetValue<string>();
await ToolAsync("openutau_add_notes", new JsonObject
{
    ["partId"] = partAfterOpen,
    ["expectedRevision"] = revision4,
    ["dryRun"] = false,
    ["notes"] = new JsonArray { new JsonObject { ["position"] = 1920, ["duration"] = 120, ["tone"] = 60 } },
});
var blocked = await ToolAsync("openutau_project_new", new JsonObject());
Check(blocked["error"]?.GetValue<string>() == "unsaved_changes", "new refuses to drop unsaved work");

// ---- error path ----------------------------------------------------------------

var bad = await ToolAsync("openutau_read_notes", new JsonObject { ["partId"] = "does-not-exist" });
Check(bad["__isError"]?.GetValue<bool>() == true, "unknown part is an error");
Check(bad["error"]?.GetValue<string>() == "invalid_request", "unknown part error code");

Console.WriteLine();
Console.WriteLine(failures.Count == 0 ? "SMOKE PASSED" : $"SMOKE FAILED ({failures.Count})");
return failures.Count == 0 ? 0 : 1;

// ---- fixtures ------------------------------------------------------------------

static UProject BuildProject()
{
    var project = new UProject { name = "Smoke Project" };
    project.tracks = new List<UTrack> { new() { TrackName = "Track1" } };
    // Loaded projects always carry the default expression set; mirror that here.
    OpenUtau.Core.Format.Ustx.AddDefaultExpressions(project);

    var part = new UVoicePart
    {
        name = "Part 1",
        trackNo = 0,
        position = 0,
        duration = 1920,
    };

    part.notes.Add(MakeNote(0, 480, 60, "a"));
    part.notes.Add(MakeNote(480, 480, 62, "i"));

    var curve = new UCurve("dyn");
    curve.xs.AddRange(new[] { 0, 480 });
    curve.ys.AddRange(new[] { 0, 10 });
    part.curves.Add(curve);

    project.parts = new List<UPart> { part };
    project.voiceParts = new List<UVoicePart> { part };
    return project;
}

static UNote MakeNote(int position, int duration, int tone, string lyric)
{
    var note = UNote.Create();
    note.position = position;
    note.duration = duration;
    note.tone = tone;
    note.lyric = lyric;
    note.pitch.data.Clear();
    note.pitch.data.Add(new PitchPoint { X = 0, Y = 0, shape = PitchPointShape.io });
    note.pitch.data.Add(new PitchPoint { X = 200, Y = 0, shape = PitchPointShape.io });
    return note;
}

static void InstallProject(UProject project, Dispatcher dispatcher)
{
    var property = typeof(DocManager).GetProperty("Project", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("DocManager.Project not found");
    var setter = property.GetSetMethod(true)
        ?? throw new InvalidOperationException("DocManager.Project has no setter");
    setter.Invoke(DocManager.Inst, new object[] { project });

    // The host normally does this in Initialize().
    var mainThread = typeof(DocManager).GetField("mainThread", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("DocManager.mainThread not found");
    mainThread.SetValue(DocManager.Inst, dispatcher.Thread);
    DocManager.Inst.PostOnUIThread = dispatcher.Post;
}

/// <summary>A classic singer that reports itself installed, so binding can be exercised without a voicebank.</summary>
internal sealed class FakeSinger : USinger
{
    public FakeSinger()
    {
        found = true;
        loaded = true;
    }

    public override string Id => "smoke-singer";
    public override string Name => "SmokeSinger";
    public override USingerType SingerType => USingerType.Classic;
}

/// <summary>A single thread that drains a queue, standing in for the editor's UI thread.</summary>
internal sealed class Dispatcher : IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<Action> queue = new();
    public Thread Thread { get; }

    public Dispatcher()
    {
        Thread = new Thread(() =>
        {
            foreach (var action in queue.GetConsumingEnumerable())
            {
                action();
            }
        })
        {
            IsBackground = true,
            Name = "smoke-dispatcher",
        };
        Thread.Start();
    }

    public void Post(Action action) => queue.Add(action);

    public void Dispose()
    {
        queue.CompleteAdding();
        Thread.Join(TimeSpan.FromSeconds(2));
    }
}


















