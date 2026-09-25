using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine.Networking;
using Verse;

namespace RimInfluence;

[StaticConstructorOnStartup]
internal static class RimTalkIntegration
{
    private static readonly ConcurrentQueue<Pawn> CompletedTalks = new ConcurrentQueue<Pawn>();
    private static readonly HashSet<string> ProcessedResponses = new HashSet<string>();

    static RimTalkIntegration()
    {
        try
        {
            var harmony = new Harmony("karasumikura.riminfluence.rimtalk");
            Type service = AccessTools.TypeByName("RimTalk.Service.TalkService");
            MethodInfo asyncGenerate = service == null ? null : AccessTools.Method(service, "GenerateAndProcessTalkAsync", new[] { AccessTools.TypeByName("RimTalk.Data.TalkRequest") });
            if (asyncGenerate != null) harmony.Patch(asyncGenerate, postfix: new HarmonyMethod(typeof(RimTalkIntegration), nameof(GenerateTalkPostfix)));

            Type openAi = AccessTools.TypeByName("RimTalk.Client.OpenAI.OpenAIClient");
            MethodInfo send = openAi == null ? null : AccessTools.Method(openAi, "SendRequestAsync");
            if (send != null) harmony.Patch(send, prefix: new HarmonyMethod(typeof(RimTalkIntegration), nameof(SendRequestPrefix)));
            Log.Message($"[RimInfluence] unified dialogue/action bridge installed: send={send != null}");
        }
        catch (Exception ex) { Log.Warning($"[RimInfluence] RimTalk native tool bridge unavailable: {ex}"); }
    }

    public static void Drain()
    {
        DialogueDiscovery.Drain();
        while (CompletedTalks.TryDequeue(out Pawn pawn)) TryReadResponses(pawn);
    }

    private static void GenerateTalkPostfix(object __0, Task __result)
    {
        if (__result == null || __0 == null) return;
        Pawn pawn = __0.GetType().GetProperty("Initiator")?.GetValue(__0) as Pawn;
        if (pawn == null) return;
        __result.ContinueWith(_ => { CompletedTalks.Enqueue(pawn); Log.Message($"[RimInfluence] RimTalk response completed for {pawn.LabelShort}; queued for parse"); }, TaskScheduler.Default);
    }

    internal static Dictionary<string, object> BuildScheduleTool(Pawn pawn, List<string> offeredIds)
    {
        List<object> actorNames = AvailableActorNames(pawn).Cast<object>().ToList();
        return new Dictionary<string, object>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object>
            {
                ["name"] = "resolve_rimworld_intent",
                ["description"] = "Return normal in-character dialogue and accepted actions in the SAME call. Decide from the initiating utterance and the dialogue you write. A direct player command is a request to attempt its action: schedule it unless the NPC explicitly refuses. Uncertain success or missing resources are game execution facts, not a refusal. NPC-to-NPC dialogue can also create new commitments. An accepted attempt or new commitment requires a Schedule assignment even when dialogue says it has begun. Clear refusal or ordinary conversation without a commitment has no assignment. Set actionDecision consistently with assignments. Use exact capability IDs and target pawn names. Defaults: immediate and Once. For each pawn's count set jobCountPerActor; for duration or repetition use a structured stop condition. Without a duration, StandStill lasts until cancelled or interrupted. Preserve the original character and language in dialogueResponses.",
                ["parameters"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["actionDecision"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new List<object> { "NoAction", "Schedule", "Cancel", "Mixed" } },
                        ["assignments"] = new Dictionary<string, object>
                        {
                            ["type"] = "array",
                            ["description"] = "One item per operation; include all receiving pawns in actors. Empty only if no action is accepted or initiated.",
                            ["items"] = BuildAssignmentSchema(actorNames)
                        },
                        ["dialogueResponses"] = new Dictionary<string, object>
                        {
                            ["type"] = "array", ["minItems"] = 1,
                            ["items"] = new Dictionary<string, object>
                            {
                                ["type"] = "object",
                                ["properties"] = new Dictionary<string, object>
                                {
                                    ["name"] = new Dictionary<string, object> { ["type"] = "string" },
                                    ["text"] = new Dictionary<string, object> { ["type"] = "string" },
                                    ["act"] = new Dictionary<string, object> { ["type"] = "string" },
                                    ["target"] = new Dictionary<string, object> { ["type"] = "string" }
                                },
                                ["required"] = new List<object> { "name", "text" }
                            }
                        }
                    },
                    ["required"] = new List<object> { "actionDecision", "assignments", "dialogueResponses" }
                }
            }
        };
    }

    private static Dictionary<string, object> BuildAssignmentSchema(List<object> actorNames)
    {
        return new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["decision"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new List<object> { "Schedule", "Cancel" } },
                ["actors"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["uniqueItems"] = true,
                    ["description"] = "All exact pawn names receiving this same operation. For a requested group, include every member here.",
                    ["items"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = actorNames }
                },
                ["capabilityId"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Exact ID from the capability directory; none is allowed only for Cancel." },
                ["targetDefName"] = new Dictionary<string, object> { ["type"] = "string" },
                ["targetQuery"] = new Dictionary<string, object> { ["type"] = "string" },
                ["targetPawnName"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Exact displayed name of the target pawn for pawn interactions such as arrest; different from actor." },
                ["recipeDefName"] = new Dictionary<string, object> { ["type"] = "string" },
                ["delayAmount"] = new Dictionary<string, object> { ["type"] = "integer", ["minimum"] = 0 },
                ["delayUnit"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new List<object> { "Minute", "Hour", "Day" } },
                ["executionMode"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new List<object> { "Once", "RepeatUntil" } },
                ["stopConditionType"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new List<object> { "None", "NoMoreTargets", "NeedLevel", "HealthPercent", "ElapsedTime", "IterationCount" } },
                ["stopConditionDefName"] = new Dictionary<string, object> { ["type"] = "string" },
                ["stopComparison"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new List<object> { "AtOrBelow", "AtOrAbove" } },
                ["stopThreshold"] = new Dictionary<string, object> { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                ["stopAmount"] = new Dictionary<string, object> { ["type"] = "integer", ["minimum"] = 0 },
                ["jobCountPerActor"] = new Dictionary<string, object> { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Number of successful jobs each listed actor must complete, or 0 if unspecified." },
                ["stopUnit"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new List<object> { "Minute", "Hour", "Day" } }
            },
            ["required"] = new List<object> { "decision", "actors", "capabilityId" }
        };
    }

    internal static List<string> AvailableActorNames(Pawn requestPawn)
    {
        if (requestPawn?.Map == null) return new List<string> { requestPawn?.LabelShort ?? "Unknown" };
        var names = requestPawn.Map.mapPawns.AllPawnsSpawned
            .Where(p => p != null && !p.Dead && p.RaceProps?.Humanlike == true && p.Faction == requestPawn.Faction)
            .Select(p => p.LabelShort).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        if (!names.Contains(requestPawn.LabelShort, StringComparer.OrdinalIgnoreCase)) names.Insert(0, requestPawn.LabelShort);
        return names;
    }

    private static bool SendRequestPrefix(object __instance, string jsonContent, DownloadHandler downloadHandler, ref Task<string> __result)
    {
        // Internal detail requests use a buffer and never re-enter the conversation bridge.
        if (downloadHandler?.GetType().FullName != "RimTalk.Client.OpenAI.OpenAIStreamHandler") return true;
        try
        {
            Type ai = AccessTools.TypeByName("RimTalk.Service.AIService");
            object request = ai?.GetProperty("CurrentRequest", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            Pawn pawn = request?.GetType().GetProperty("Initiator")?.GetValue(request) as Pawn;
            if (pawn == null) return true;
            string rawPrompt = request.GetType().GetProperty("RawPrompt")?.GetValue(request) as string ?? "";
            if (rawPrompt.IndexOf("任务失败反馈", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            __result = DialogueDiscovery.ResolveAsync(__instance, jsonContent, downloadHandler, pawn, rawPrompt);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("[RimInfluence] unified request preparation failed: " + ex);
            __result = Task.FromException<string>(ex);
            return false;
        }
    }

    internal static bool ProcessAssignments(Pawn requestPawn, IDictionary<string, object> root, string raw, HashSet<string> offeredIds = null)
    {
        if (root == null || !root.TryGetValue("assignments", out object value) || !(value is IEnumerable items)) return false;
        int operations = 0;
        int recipients = 0;
        int accepted = 0;
        foreach (object item in items)
        {
            if (!(item is IDictionary<string, object> assignment)) continue;
            operations++;
            Log.Message("[RimInfluence] structured assignment: " + RimTalkJson.Serialize(assignment));
            string decision = Value(assignment, "decision");
            string capabilityId = Value(assignment, "capabilityId");
            if (offeredIds != null && string.Equals(decision, "Schedule", StringComparison.OrdinalIgnoreCase)
                && !offeredIds.Contains(capabilityId))
            {
                Log.Warning($"[RimInfluence] assignment rejected: capability '{capabilityId}' was not present in the capability catalog");
                continue;
            }
            List<string> actors = AssignmentActors(assignment);
            if (actors.Count == 0)
            {
                Log.Warning("[RimInfluence] assignment rejected: no actors were supplied");
                continue;
            }
            foreach (string actor in actors)
            {
                recipients++;
                var recipientAssignment = UnifiedDialogueProtocol.WithDefaults(assignment);
                recipientAssignment["actor"] = actor;
                bool result;
                if (string.Equals(decision, "Schedule", StringComparison.OrdinalIgnoreCase))
                    result = RimInfluenceApi.TrySchedule(requestPawn, recipientAssignment, raw);
                else if (string.Equals(decision, "Cancel", StringComparison.OrdinalIgnoreCase))
                    result = RimInfluenceApi.TryCancel(requestPawn, recipientAssignment);
                else
                {
                    Log.Warning($"[RimInfluence] assignment rejected: unsupported decision '{decision}'");
                    break;
                }
                if (result) accepted++;
            }
        }
        string rootDecision = Value(root, "decision");
        Log.Message($"[RimInfluence] assignment batch received for {requestPawn?.LabelShort ?? "unknown pawn"}: operations={operations} recipients={recipients} accepted={accepted} rootDecision={rootDecision} rootCapability={Value(root, "capabilityId")}");
        return operations > 0 || !(rootDecision.Equals("Schedule", StringComparison.OrdinalIgnoreCase)
            || rootDecision.Equals("Cancel", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> AssignmentActors(IDictionary<string, object> assignment)
    {
        var actors = new List<string>();
        if (assignment.TryGetValue("actors", out object value) && value is IEnumerable values && !(value is string))
        {
            foreach (object item in values)
            {
                string name = item?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(name) && !actors.Contains(name, StringComparer.OrdinalIgnoreCase)) actors.Add(name);
            }
        }
        string legacyActor = Value(assignment, "actor").Trim();
        if (actors.Count == 0 && !string.IsNullOrWhiteSpace(legacyActor)) actors.Add(legacyActor);
        return actors;
    }

    private static string Value(IDictionary<string, object> dictionary, string key)
    {
        return dictionary != null && dictionary.TryGetValue(key, out object value) ? value?.ToString() ?? "" : "";
    }

    private static void TryReadResponses(Pawn pawn)
    {
        try
        {
            Type cache = AccessTools.TypeByName("RimTalk.Data.Cache");
            object state = cache?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { pawn });
            object responses = state?.GetType().GetField("TalkResponses")?.GetValue(state) ?? state?.GetType().GetProperty("TalkResponses")?.GetValue(state);
            if (!(responses is IEnumerable enumerable)) return;
            foreach (object response in enumerable)
            {
                string text = response?.GetType().GetProperty("Text")?.GetValue(response) as string;
                if (string.IsNullOrEmpty(text)) continue;
                string responseId = response?.GetType().GetProperty("Id")?.GetValue(response)?.ToString() ?? text.GetHashCode().ToString();
                if (!ProcessedResponses.Add(pawn.thingIDNumber + ":" + responseId)) continue;
                Log.Message($"[RimInfluence] response text for {pawn.LabelShort}: {text.Substring(0, Math.Min(160, text.Length))}");
            }
        }
        catch (Exception ex) { Log.Warning($"[RimInfluence] Could not inspect RimTalk response: {ex.Message}"); }
    }
}
