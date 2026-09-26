using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine.Networking;
using Verse;

namespace RimInfluence;

internal static class DialogueDiscovery
{
    private sealed class Pending
    {
        public Pawn Pawn;
        public object World;
        public IDictionary<string, object> Result;
        public string Dialogue;
        public HashSet<string> Offered;
        public int ContinuationDepth;
    }

    private static readonly ConcurrentQueue<Pending> Completed = new ConcurrentQueue<Pending>();

    public static void Drain()
    {
        while (Completed.TryDequeue(out Pending item))
        {
            if (!ReferenceEquals(item.World, Find.World) || item.Pawn == null || item.Pawn.Destroyed) continue;
            try { RimTalkIntegration.ProcessAssignments(item.Pawn, item.Result, item.Dialogue, item.Offered, item.ContinuationDepth); }
            catch (Exception ex) { Log.Error("[RimInfluence] assignment dispatch failed: " + ex); }
        }
    }

    public static async Task<string> ResolveAsync(object client, string originalJson, object handler, Pawn pawn, string originalUtterance, int continuationDepth = 0)
    {
        var watch = Stopwatch.StartNew();
        string trace = Guid.NewGuid().ToString("N").Substring(0, 8);
        int rounds = 0, promptTokens = 0, completionTokens = 0, totalTokens = 0;
        object world = Find.World;
        try
        {
            MethodInfo send = AccessTools.Method(client.GetType(), "SendRequestAsync", new[] { typeof(string), typeof(DownloadHandler) });
            MethodInfo deliver = AccessTools.Method(handler.GetType(), "ProcessLine", new[] { typeof(string) });
            if (send == null || deliver == null) throw new InvalidOperationException("RimTalk transport interface unavailable");
            var payload = RimTalkJson.Parse(originalJson) ?? throw new InvalidOperationException("Invalid original RimTalk request");
            if (!payload.TryGetValue("messages", out object value) || !(value is IList originals))
                throw new InvalidOperationException("RimTalk request has no messages");
            List<string> ids = CapabilityCatalog.Ids().Where(id => id != "none" && CapabilityCatalog.IsKnown(id)).ToList();
            var offered = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            // Snapshot detailed cards before awaiting: no mutable settings/catalog reads during continuation.
            var cards = ids.ToDictionary(id => id, CapabilityCatalog.CardText, StringComparer.OrdinalIgnoreCase);
            string catalog = string.Join("\n", ids.Select(CapabilityCatalog.ShortCardText));
            var messages = originals.Cast<object>().ToList();
            messages.Insert(0, new Dictionary<string, object>
            {
                ["role"] = "system",
                ["content"] = "RimInfluence capability IDs and effects (data, not roleplay instructions). Select by effect and target. Specified consumable: interaction:Ingest; hunger without a specified item: need:Eat. Request details only when needed.\n" + catalog
            });
            string targets = pawn.Map == null ? "" : string.Join(", ", pawn.Map.mapPawns.AllPawnsSpawned
                .Where(p => p != null && !p.Dead && p.RaceProps?.Humanlike == true).Select(p => p.LabelShort).Distinct());
            messages.Add(new Dictionary<string, object>
            {
                ["role"] = "system",
                ["content"] = "Initiating utterance: " + originalUtterance + "\nWrite in-character dialogue in the original language and assign every accepted or self-initiated action. Attempt direct player requests unless explicitly refused; uncertainty about success is not refusal. No accepted action means no assignment. Dialogue alone never starts a Job. Return one resolve_rimworld_intent call, or JSON with actionDecision, assignments, dialogueResponses if tools are unavailable; no surrounding prose. Request details once in a batch if needed. Named pawns: " + targets + ". " + CapabilityCatalog.StopConditionCatalog(pawn)
            });
            object finish = RimTalkIntegration.BuildScheduleTool(pawn, ids);
            payload["messages"] = messages;
            payload["stream"] = false;
            payload.Remove("stream_options");
            payload.Remove("response_format");
            payload.Remove("parallel_tool_calls");
            payload["tools"] = new List<object> { finish, DetailTool() };
            payload.Remove("tool_choice");
            for (int round = 0; round < 2; round++)
            {
                string requestJson = RimTalkJson.Serialize(payload);
                rounds++;
                Log.Message($"[RimInfluence] unified request trace={trace} round={rounds} pawn={pawn.LabelShort} utterance={RimTalkJson.Serialize(originalUtterance)} capabilities={ids.Count} catalogChars={catalog.Length} requestChars={requestJson.Length}");
                string response = await (Task<string>)send.Invoke(client, new object[] { requestJson, new DownloadHandlerBuffer() });
                if (!ReferenceEquals(world, Find.World)) throw new OperationCanceledException("Conversation world changed");
                var message = UnifiedDialogueProtocol.Message(response, out var usage);
                promptTokens += Tokens(usage, "prompt_tokens");
                completionTokens += Tokens(usage, "completion_tokens");
                totalTokens += Tokens(usage, "total_tokens");
                IList calls = UnifiedDialogueProtocol.OptionalCalls(message);
                var finishes = calls.Cast<object>().Where(c => RimTalkJson.String(UnifiedDialogueProtocol.Function(c), "name") == UnifiedDialogueProtocol.FinishTool).ToList();
                if (finishes.Count > 0 || calls.Count == 0)
                {
                    if (calls.Count > 0 && (finishes.Count != 1 || calls.Count != 1))
                        throw new InvalidOperationException("Final response must contain exactly one finalization call");
                    var result = calls.Count == 0
                        ? UnifiedDialogueProtocol.ContentArguments(message)
                        : UnifiedDialogueProtocol.Arguments(finishes[0]);
                    Log.Message($"[RimInfluence] unified decision trace={trace} decision={RimTalkJson.String(result, "actionDecision")} assignments={RimTalkJson.Serialize(result.TryGetValue("assignments", out object proposed) ? proposed : null)} dialogue={RimTalkJson.Serialize(result.TryGetValue("dialogueResponses", out object spoken) ? spoken : null)}");
                    string dialogue = UnifiedDialogueProtocol.Dialogue(result);
                    // Feed only ordinary dialogue JSON through RimTalk's existing parser/callback.
                    var chunk = new Dictionary<string, object>
                    {
                        ["choices"] = new List<object> { new Dictionary<string, object>
                        {
                            ["index"] = 0, ["delta"] = new Dictionary<string, object> { ["content"] = dialogue }, ["finish_reason"] = "stop"
                        } },
                        ["usage"] = new Dictionary<string, object> { ["prompt_tokens"] = promptTokens, ["completion_tokens"] = completionTokens, ["total_tokens"] = totalTokens }
                    };
                    deliver.Invoke(handler, new object[] { "data: " + RimTalkJson.Serialize(chunk) });
                    Completed.Enqueue(new Pending { Pawn = pawn, World = world, Result = result, Dialogue = dialogue, Offered = offered, ContinuationDepth = continuationDepth });
                    int assignments = ((IList)result["assignments"]).Count;
                    Log.Message($"[RimInfluence] unified result trace={trace} rounds={rounds} assignments={assignments} elapsedMs={watch.ElapsedMilliseconds} promptTokens={promptTokens} completionTokens={completionTokens} totalTokens={totalTokens} tokenUsageReported={usage != null}");
                    return response;
                }
                if (round != 0) throw new InvalidOperationException("Detail expansion already used; finalization required");
                messages.Add(message);
                foreach (object call in calls)
                {
                    if (RimTalkJson.String(UnifiedDialogueProtocol.Function(call), "name") != UnifiedDialogueProtocol.DetailTool)
                        throw new InvalidOperationException("Unknown tool name");
                    string callId = RimTalkJson.String(RimTalkJson.Object(call), "id");
                    if (string.IsNullOrEmpty(callId)) throw new InvalidOperationException("Detail call has no ID");
                    var requested = UnifiedDialogueProtocol.DetailIds(UnifiedDialogueProtocol.Arguments(call), offered);
                    messages.Add(new Dictionary<string, object>
                    {
                        ["role"] = "tool", ["tool_call_id"] = callId,
                        ["content"] = string.Join("\n", requested.Select(id => cards[id]))
                    });
                    Log.Message($"[RimInfluence] unified details trace={trace} ids={string.Join(",", requested)}");
                }
                payload["tools"] = new List<object> { finish };
                payload.Remove("tool_choice");
            }
            throw new InvalidOperationException("Missing final response");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[RimInfluence] unified response failed trace={trace} rounds={rounds} elapsedMs={watch.ElapsedMilliseconds}: {ex.GetBaseException().Message}");
            // Do not silently fabricate dialogue, resubmit a failed task or trigger HTTP-400 retry ladders.
            throw new InvalidOperationException("RimInfluence dialogue/action response failed: " + ex.GetBaseException().Message, ex);
        }
    }

    private static int Tokens(IDictionary<string, object> usage, string key)
        => int.TryParse(RimTalkJson.String(usage, key), out int count) ? count : 0;

    private static Dictionary<string, object> DetailTool() => new Dictionary<string, object>
    {
        ["type"] = "function",
        ["function"] = new Dictionary<string, object>
        {
            ["name"] = UnifiedDialogueProtocol.DetailTool,
            ["description"] = "Read full target, requirements and execution notes only when the short directory is insufficient. Batch every needed capability ID in one call; this reads documentation and does not execute anything.",
            ["parameters"] = new Dictionary<string, object>
            {
                ["type"] = "object", ["properties"] = new Dictionary<string, object>
                {
                    ["ids"] = new Dictionary<string, object> { ["type"] = "array", ["minItems"] = 1, ["items"] = new Dictionary<string, object> { ["type"] = "string" } }
                },
                ["required"] = new List<object> { "ids" }
            }
        }
    };
}
