using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using RimInfluence;

// The real bridge/protocol runs against an in-memory transport. No game or paid API call needed.
internal static class TransportTests
{
    private sealed class Client
    {
        internal readonly List<string> Requests = new List<string>();
        internal readonly Queue<string> Responses = new Queue<string>();
        private Task<string> SendRequestAsync(string json, UnityEngine.Networking.DownloadHandler handler)
        {
            if (RimTalkJson.Parse(json).ContainsKey("tool_choice"))
                throw new InvalidOperationException("Thinking mode does not support this tool_choice");
            Requests.Add(json);
            return Task.FromResult(Responses.Dequeue());
        }
    }
    private sealed class Handler
    {
        internal string Content;
        internal int Deliveries;
        private void ProcessLine(string line)
        {
            Deliveries++;
            var root = RimTalkJson.Parse(line.Substring(6));
            var choice = RimTalkJson.Object(((IList)root["choices"])[0]);
            Content = RimTalkJson.String(RimTalkJson.Object(choice["delta"]), "content");
        }
    }
    private const string Original = "{\"model\":\"test\",\"stream\":true,\"response_format\":{\"type\":\"json_object\"},\"messages\":[{\"role\":\"system\",\"content\":\"original personality\"},{\"role\":\"user\",\"content\":\"original conversation\"}]}";
    private const string Final = "{\"actionDecision\":\"Schedule\",\"dialogueResponses\":[{\"name\":\"Arthur\",\"text\":\"我在这里等你\"}],\"assignments\":[{\"decision\":\"Schedule\",\"actors\":[\"Arthur\"],\"capabilityId\":\"command:StandStill\"}]}";
    private static int checks;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL transport: " + name); checks++; }
    private static string Response(string tool, string args) => RimTalkJson.Serialize(new Dictionary<string, object>
    {
        ["choices"] = new List<object> { new Dictionary<string, object>
        {
            ["finish_reason"] = "tool_calls", ["message"] = new Dictionary<string, object>
            {
                ["role"] = "assistant", ["tool_calls"] = new List<object> { new Dictionary<string, object>
                {
                    ["id"] = "call_1", ["type"] = "function", ["function"] = new Dictionary<string, object> { ["name"] = tool, ["arguments"] = args }
                } }
            }
        } }
    });
    private static string ContentResponse(string content) => RimTalkJson.Serialize(new Dictionary<string, object>
    {
        ["choices"] = new List<object> { new Dictionary<string, object>
        {
            ["finish_reason"] = "stop", ["message"] = new Dictionary<string, object> { ["role"] = "assistant", ["content"] = content }
        } }
    });
    internal static void Run()
    {
        var pawn = new Verse.Pawn();
        var client = new Client(); var handler = new Handler();
        client.Responses.Enqueue(Response(UnifiedDialogueProtocol.FinishTool, Final));
        DialogueDiscovery.ResolveAsync(client, Original, handler, pawn, "请站着不动").GetAwaiter().GetResult();
        Check(client.Requests.Count == 1 && handler.Deliveries == 1, "normal path uses one request and one dialogue delivery");
        Check(handler.Content.Contains("我在这里等你") && !handler.Content.Contains("assignments"), "dialogue is isolated");
        Check(RimTalkIntegration.Dispatched == 0, "no off-thread scheduling");
        DialogueDiscovery.Drain(); DialogueDiscovery.Drain();
        Check(RimTalkIntegration.Dispatched == 1, "main thread dispatch exactly once");
        var payload = RimTalkJson.Parse(client.Requests[0]);
        string messages = RimTalkJson.Serialize(payload["messages"]);
        Check(messages.Contains("original personality") && messages.Contains("original conversation") && messages.Contains("请站着不动"), "original prompt and initiating utterance preserved");
        Check(messages.Contains("work:NewModTask") && messages.Contains("command:StandStill"), "complete short directory present");
        Check(!payload.ContainsKey("response_format") && (bool)payload["stream"] == false, "no conflicting response format");
        Check(!payload.ContainsKey("tool_choice") && !payload.ContainsKey("parallel_tool_calls"), "thinking-compatible request omits tool choice");
        client = new Client(); handler = new Handler();
        client.Responses.Enqueue(Response(UnifiedDialogueProtocol.DetailTool, "{\"ids\":[\"work:NewModTask\",\"command:StandStill\"]}"));
        client.Responses.Enqueue(Response(UnifiedDialogueProtocol.FinishTool, Final));
        DialogueDiscovery.ResolveAsync(client, Original, handler, pawn, "请站着不动").GetAwaiter().GetResult();
        Check(client.Requests.Count == 2 && handler.Deliveries == 1, "detail path adds exactly one request");
        payload = RimTalkJson.Parse(client.Requests[1]);
        messages = RimTalkJson.Serialize(payload["messages"]);
        Check(messages.Contains("tool_call_id") && messages.Contains("FULL DETAILS work:NewModTask"), "real tool-result continuation");
        Check(((IList)payload["tools"]).Count == 1, "second round only allows finalization");
        Check(!payload.ContainsKey("tool_choice"), "detail continuation also omits tool choice");
        DialogueDiscovery.Drain();
        client = new Client(); handler = new Handler();
        client.Responses.Enqueue(ContentResponse(Final));
        DialogueDiscovery.ResolveAsync(client, Original, handler, pawn, "请站着不动").GetAwaiter().GetResult();
        Check(client.Requests.Count == 1 && handler.Content.Contains("我在这里等你"), "thinking model plain JSON response delivers dialogue");
        DialogueDiscovery.Drain();
        Check(RimTalkIntegration.Dispatched == 3, "thinking model plain JSON response dispatches action");
        client = new Client(); handler = new Handler();
        client.Responses.Enqueue(Response(UnifiedDialogueProtocol.FinishTool, Final));
        DialogueDiscovery.ResolveAsync(client, Original, handler, pawn, "previous execution failed", 2).GetAwaiter().GetResult();
        DialogueDiscovery.Drain();
        Check(RimTalkIntegration.LastContinuationDepth == 2, "continuation depth reaches assignment dispatch");
        client = new Client(); handler = new Handler();
        client.Responses.Enqueue(Response(UnifiedDialogueProtocol.DetailTool, "{\"ids\":[\"invented\"]}"));
        bool failed = false;
        try { DialogueDiscovery.ResolveAsync(client, Original, handler, pawn, "请站着不动").GetAwaiter().GetResult(); } catch (InvalidOperationException) { failed = true; }
        Check(failed && client.Requests.Count == 1 && handler.Deliveries == 0, "unknown tool IDs fail without retries or fake dialogue");
        client = new Client(); handler = new Handler();
        client.Responses.Enqueue(Response(UnifiedDialogueProtocol.FinishTool, Final));
        DialogueDiscovery.ResolveAsync(client, Original, handler, pawn, "请站着不动").GetAwaiter().GetResult();
        Verse.Find.World = new object();
        DialogueDiscovery.Drain();
        Check(RimTalkIntegration.Dispatched == 4, "stale world response never dispatches");
        Console.WriteLine("PASS " + checks + " bridge checks (real bridge, simulated transport)");
    }
}

namespace UnityEngine.Networking
{
    public class DownloadHandler { }
    public class DownloadHandlerBuffer : DownloadHandler { }
}
namespace Verse
{
    public sealed class Pawn
    {
        public bool Destroyed;
        public bool Dead;
        public string LabelShort = "Arthur";
        public Map Map;
        public RaceProperties RaceProps = new RaceProperties();
    }
    public sealed class RaceProperties { public bool Humanlike = true; }
    public sealed class Map { public MapPawns mapPawns = new MapPawns(); }
    public sealed class MapPawns { public List<Pawn> AllPawnsSpawned = new List<Pawn>(); }
    public static class Find { public static object World = new object(); }
    public static class Log { public static void Message(string text) { } public static void Error(string text) { } }
}
namespace RimInfluence
{
    internal static class CapabilityCatalog
    {
        public static List<string> Ids() => new List<string> { "none", "command:StandStill", "work:NewModTask" };
        public static bool IsKnown(string id) => Ids().Contains(id);
        public static string CardText(string id) => "FULL DETAILS " + id;
        public static string ShortCardText(string id) => id + " | short effect";
        public static string StopConditionCatalog(Verse.Pawn pawn) => "Stop conditions";
    }
    internal static class RimTalkIntegration
    {
        public static int Dispatched;
        public static int LastContinuationDepth;
        public static object BuildScheduleTool(Verse.Pawn pawn, List<string> ids) => new Dictionary<string, object>
        {
            ["type"] = "function", ["function"] = new Dictionary<string, object> { ["name"] = UnifiedDialogueProtocol.FinishTool }
        };
        public static void ProcessAssignments(Verse.Pawn pawn, IDictionary<string, object> result, string dialogue, HashSet<string> offered, int continuationDepth)
        { Dispatched++; LastContinuationDepth = continuationDepth; }
    }
}
