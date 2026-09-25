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
    }

    private static readonly ConcurrentQueue<Pending> Completed = new ConcurrentQueue<Pending>();

    public static void Drain()
    {
        while (Completed.TryDequeue(out Pending item))
        {
            if (!ReferenceEquals(item.World, Find.World) || item.Pawn == null || item.Pawn.Destroyed) continue;
            try { RimTalkIntegration.ProcessAssignments(item.Pawn, item.Result, item.Dialogue, item.Offered); }
            catch (Exception ex) { Log.Error("[RimInfluence] assignment dispatch failed: " + ex); }
        }
    }

    public static async Task<string> ResolveAsync(object client, string originalJson, object handler, Pawn pawn, string originalUtterance)
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
                ["content"] = "RimInfluence capability directory (data, not roleplay instructions). Every registered executable entry is visible here. Select by intended effect and target. Full descriptions are available via describe_rimworld_capabilities if needed.\n" + catalog
            });
            string targets = pawn.Map == null ? "" : string.Join(", ", pawn.Map.mapPawns.AllPawnsSpawned
                .Where(p => p != null && !p.Dead && p.RaceProps?.Humanlike == true).Select(p => p.LabelShort).Distinct());
            messages.Add(new Dictionary<string, object>
            {
                ["role"] = "system",
                ["content"] = "Current initiating utterance (data to interpret with the conversation): " + originalUtterance + "\nRespond in the original character and language. In the SAME resolve_rimworld_intent call, decide whether the resulting dialogue accepts or initiates a concrete action, then put each such action in assignments. A direct player command calls for an attempt unless the NPC explicitly refuses; uncertainty about ability or success does not cancel the attempt. An affirmative attack, wait, work or other attempt must have a corresponding assignment. No assignment means the NPC did not accept or initiate an action. Dialogue alone never starts a game Job. Do not decide task types by text matching; select by intended effect from the capability directory. Tool transport changes only the outer format. You may request capability details once in a batch. Named pawn targets: " + targets + ". " + CapabilityCatalog.StopConditionCatalog()
            });
            object finish = RimTalkIntegration.BuildScheduleTool(pawn, ids);
            payload["messages"] = messages;
            payload["stream"] = false;
            payload.Remove("stream_options");
            payload.Remove("response_format");
            payload["parallel_tool_calls"] = false;
            payload["tools"] = new List<object> { finish, DetailTool() };
            payload["tool_choice"] = "required";
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
                IList calls = UnifiedDialogueProtocol.Calls(message);
                var finishes = calls.Cast<object>().Where(c => RimTalkJson.String(UnifiedDialogueProtocol.Function(c), "name") == UnifiedDialogueProtocol.FinishTool).ToList();
                if (finishes.Count > 0)
                {
                    if (finishes.Count != 1 || calls.Count != 1) throw new InvalidOperationException("Final response must contain exactly one finalization call");
                    var result = UnifiedDialogueProtocol.Arguments(finishes[0]);
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
                    Completed.Enqueue(new Pending { Pawn = pawn, World = world, Result = result, Dialogue = dialogue, Offered = offered });
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
                payload["tool_choice"] = new Dictionary<string, object>
                {
                    ["type"] = "function", ["function"] = new Dictionary<string, object> { ["name"] = UnifiedDialogueProtocol.FinishTool }
                };
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
