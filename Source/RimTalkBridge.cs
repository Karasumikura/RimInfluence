using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimInfluence;

internal static class RimTalkBridge
{
    private static readonly ConcurrentDictionary<object, int> Continuations = new ConcurrentDictionary<object, int>();

    public static bool TryGetContinuation(object request, out int depth)
    {
        depth = 0;
        return request != null && Continuations.TryRemove(request, out depth);
    }

    public static bool TryContinue(Pawn pawn, ScheduledTask task)
    {
        bool succeeded = task.Status == ScheduledTaskStatus.Completed;
        string observation = $"RimInfluence execution result: {(succeeded ? "completed" : "failed")}. Actor: {pawn.LabelShort}. Capability: {task.CapabilityId}. "
            + $"Requested target: {task.TargetDefName} {task.TargetQuery}. Native job: {task.LastJobDefName}. "
            + (succeeded ? $"Completed jobs: {task.CompletedIterations}. "
                : $"Failure kind: {task.LastExecutionFailureKind}. Actual reason: {task.FailureReason}. ")
            + (succeeded ? "" : TargetObservation(pawn, task))
            + $"Original dialogue: {task.SourceDialogue}. Respond in the original character and language to the observed result. "
            + "Only assign a new action if needed to fulfill the original intent; never repeat a completed action merely because it was originally requested. Do not claim an uncompleted action succeeded.";
        return Send(pawn, observation, "Event", task.ContinuationDepth + 1);
    }

    private static string TargetObservation(Pawn pawn, ScheduledTask task)
    {
        if (pawn.Map == null || string.IsNullOrWhiteSpace(task.TargetDefName)) return "";
        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(task.TargetDefName);
        if (def == null) return "";
        try
        {
            var targets = pawn.Map.listerThings.AllThings.Where(thing => thing.Spawned && thing.def == def).ToList();
            if (targets.Count == 0) return "Target observation: no matching item currently spawned. ";
            int forbidden = targets.Count(thing => thing.IsForbidden(pawn));
            int unreachable = targets.Count(thing => !thing.IsForbidden(pawn)
                && !pawn.CanReach(thing, PathEndMode.Touch, Danger.Some));
            int reserved = targets.Count(thing => !thing.IsForbidden(pawn)
                && pawn.CanReach(thing, PathEndMode.Touch, Danger.Some) && !pawn.CanReserve(thing));
            return $"Target observation: {targets.Count} matching; {forbidden} forbidden, {unreachable} unreachable, {reserved} reserved. ";
        }
        catch (Exception ex)
        {
            Log.Warning("[RimInfluence] target observation unavailable: " + ex.GetBaseException().Message);
            return "";
        }
    }

    public static bool TryReportFailure(Pawn pawn, string original, string failure)
    {
        return Send(pawn, $"任务失败反馈：{failure}\n原计划：{original}\n这是游戏执行器返回的实际结果。请按原有角色设定和对话格式告诉玩家任务没有完成及原因，不要虚构已经成功，也不要安排新任务。", "Event", -1);
    }

    public static bool TryWake(Pawn pawn, string original, string failure)
    {
        return Send(pawn, $"任务失败：{failure}\n原计划：{original}\n请说明具体原因，并提出下一步可执行安排。", "Event");
    }

    private static bool Send(Pawn pawn, string prompt, string talkTypeName, int continuationDepth = 0)
    {
        if (pawn == null) return false;
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RimTalk");
            var busy = asm?.GetType("RimTalk.Service.AIService")?.GetMethod("IsBusy", BindingFlags.Public | BindingFlags.Static);
            if (busy == null || (bool)busy.Invoke(null, null)) return false;
            var requestType = asm?.GetType("RimTalk.Data.TalkRequest");
            var serviceType = asm?.GetType("RimTalk.Service.TalkService");
            var talkType = asm?.GetType("RimTalk.Source.Data.TalkType");
            var generate = serviceType?.GetMethod("GenerateTalk", BindingFlags.Public | BindingFlags.Static);
            if (requestType == null || generate == null) return false;
            object request = Activator.CreateInstance(requestType, prompt, pawn, null, Enum.Parse(talkType, talkTypeName));
            if (continuationDepth != 0) Continuations[request] = continuationDepth;
            bool accepted = (bool)generate.Invoke(null, new[] { request });
            if (!accepted) Continuations.TryRemove(request, out _);
            if (accepted) Log.Message($"[RimInfluence] outcome dialogue submitted pawn={pawn.LabelShort} continuationDepth={continuationDepth}");
            return accepted;
        }
        catch (Exception ex) { Log.Warning($"[RimInfluence] RimTalk failure report failed: {ex.Message}"); return false; }
    }
}
