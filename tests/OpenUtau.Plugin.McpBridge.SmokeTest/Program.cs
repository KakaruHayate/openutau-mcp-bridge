using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Plugin.McpBridge;

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
Check(status["bridge"]?["protocol"]?.GetValue<int>() == 1, "status protocol");
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

