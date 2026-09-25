using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimInfluence;

internal static class DynamicCapabilityExecutor
{
    public static bool Handles(string id) => !string.IsNullOrWhiteSpace(id)
        && (id.StartsWith("work:", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("need:", StringComparison.OrdinalIgnoreCase));

    public static bool TryStart(ScheduledTask task)
    {
        if (task.CapabilityId.Equals("need:Eat", StringComparison.OrdinalIgnoreCase)) return TryEat(task);
        if (task.CapabilityId.StartsWith("work:", StringComparison.OrdinalIgnoreCase)) return TryWorkGiver(task);
        return false;
    }

    private static bool TryEat(ScheduledTask task)
    {
        try
        {
            var giver = new JobGiver_GetFood { forceScanWholeMap = true };
            MethodInfo method = typeof(JobGiver_GetFood).GetMethod("TryGiveJob", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(Pawn) }, null);
            Job job = method?.Invoke(giver, new object[] { task.Pawn }) as Job;
            if (job == null)
            {
                task.LastExecutionError = "游戏的食物选择器没有找到该角色可以食用并能够到达的食物。";
                task.LastExecutionFailureKind = TaskExecutionFailureKind.NoExecutableTarget;
                return false;
            }
            return Take(task, job, "吃东西");
        }
        catch (Exception ex)
        {
            task.LastExecutionError = "原生进食任务构造失败：" + ex.GetBaseException().Message;
            Log.Warning("[RimInfluence] native eat resolver failed: " + ex);
            return false;
        }
    }

    private static bool TryWorkGiver(ScheduledTask task)
    {
        string defName = task.CapabilityId.Substring(5);
        WorkGiverDef def = DefDatabase<WorkGiverDef>.GetNamedSilentFail(defName);
        if (def?.giverClass == null)
        {
            task.LastExecutionError = "WorkGiverDef 不存在或没有执行器：" + defName;
            return false;
        }
        try
        {
            WorkGiver worker = def.Worker;
            if (worker == null)
            {
                task.LastExecutionError = "WorkGiver 无法实例化：" + defName;
                return false;
            }
            if (worker.MissingRequiredCapacity(task.Pawn) is PawnCapacityDef capacity)
            {
                task.LastExecutionError = "角色缺少执行此工作的能力：" + capacity.LabelCap;
                return false;
            }
            JobFailReason.Clear();
            if (worker.ShouldSkip(task.Pawn, true))
            {
                task.LastExecutionError = ReadNativeFailure("游戏判定当前应跳过此工作。");
                task.LastExecutionFailureKind = TaskExecutionFailureKind.NoExecutableTarget;
                return false;
            }

            if (!(worker is WorkGiver_Scanner scanner))
            {
                Job nonScan = worker.NonScanJob(task.Pawn);
                if (nonScan == null)
                {
                    task.LastExecutionError = "该 WorkGiver 当前没有可执行任务。";
                    task.LastExecutionFailureKind = TaskExecutionFailureKind.NoExecutableTarget;
                    return false;
                }
                nonScan.workGiverDef = def;
                return Take(task, nonScan, def.gerund);
            }

            Job job = FindThingJob(task, scanner) ?? FindCellJob(task, scanner);
            if (job == null)
            {
                task.LastExecutionError = ReadNativeFailure("WorkGiver 已加载，但当前没有通过其原生校验的目标。");
                task.LastExecutionFailureKind = TaskExecutionFailureKind.NoExecutableTarget;
                return false;
            }
            job.workGiverDef = def;
            return Take(task, job, def.gerund);
        }
        catch (Exception ex)
        {
            task.LastExecutionError = "WorkGiver 执行失败：" + ex.GetBaseException().Message;
            Log.Warning($"[RimInfluence] dynamic WorkGiver failed def={defName}: {ex}");
            return false;
        }
    }

    private static Job FindThingJob(ScheduledTask task, WorkGiver_Scanner scanner)
    {
        IEnumerable<Thing> candidates = scanner.PotentialWorkThingsGlobal(task.Pawn);
        if (candidates == null)
        {
            ThingRequest request = scanner.PotentialWorkThingRequest;
            candidates = request.IsUndefined ? Enumerable.Empty<Thing>()
                : task.Pawn.Map.listerThings.ThingsMatching(request);
        }
        foreach (Thing thing in candidates.Where(t => Match(task, t))
                     .OrderBy(t => task.Pawn.Position.DistanceToSquared(t.Position)).Take(512))
        {
            Job job;
            try
            {
                JobFailReason.Clear();
                job = scanner.JobOnThing(task.Pawn, thing, true);
                if (job == null) CaptureNativeFailure(task);
            }
            catch { continue; }
            if (job != null) return job;
        }
        return null;
    }

    private static Job FindCellJob(ScheduledTask task, WorkGiver_Scanner scanner)
    {
        IEnumerable<IntVec3> cells;
        try { cells = scanner.PotentialWorkCellsGlobal(task.Pawn); }
        catch { return null; }
        if (cells == null) return null;
        foreach (IntVec3 cell in cells.OrderBy(c => task.Pawn.Position.DistanceToSquared(c)).Take(2048))
        {
            Job job;
            try
            {
                JobFailReason.Clear();
                job = scanner.JobOnCell(task.Pawn, cell, true);
                if (job == null) CaptureNativeFailure(task);
            }
            catch { continue; }
            if (job != null) return job;
        }
        return null;
    }

    private static void CaptureNativeFailure(ScheduledTask task)
    {
        if (JobFailReason.HaveReason && !string.IsNullOrWhiteSpace(JobFailReason.Reason))
            task.LastExecutionError = JobFailReason.Reason;
        else if (!string.IsNullOrWhiteSpace(JobFailReason.CustomJobString))
            task.LastExecutionError = JobFailReason.CustomJobString;
    }

    private static string ReadNativeFailure(string fallback)
    {
        if (JobFailReason.HaveReason && !string.IsNullOrWhiteSpace(JobFailReason.Reason)) return JobFailReason.Reason;
        if (!string.IsNullOrWhiteSpace(JobFailReason.CustomJobString)) return JobFailReason.CustomJobString;
        return fallback;
    }

    private static bool Match(ScheduledTask task, Thing thing)
    {
        if (thing == null || !thing.Spawned || thing.IsForbidden(task.Pawn) || !task.Pawn.CanReserve(thing)) return false;
        if (!string.IsNullOrWhiteSpace(task.TargetDefName)
            && !thing.def.defName.Equals(task.TargetDefName, StringComparison.OrdinalIgnoreCase)) return false;
        string query = task.TargetQuery;
        if (string.IsNullOrWhiteSpace(query)) return true;
        return thing.def.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || thing.LabelCap.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || thing.LabelShort.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool Take(ScheduledTask task, Job job, string label)
    {
        if (job == null) return false;
        JobDef expectedDef = job.def;
        job.playerForced = true;
        if (RimInfluenceMod.Settings.MarkTriggeredJobs)
            job.reportStringOverride = "RimInfluence：正在" + (label.NullOrEmpty() ? CapabilityCatalog.Label(task.CapabilityId, task.Action) : label);
        bool accepted = task.Pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        if (accepted)
        {
            Job current = task.Pawn.jobs.curJob;
            if (job.def == expectedDef && current != null && current.JobIsSameAs(task.Pawn, job)) task.ActiveJob = current;
            else if (job.def == expectedDef && task.Pawn.jobs.jobQueue.Contains(job)) task.ActiveJob = job;
            else
            {
                task.LastExecutionError = "游戏接受派发后立即丢弃了原生 Job，未进入执行或等待队列。";
                task.LastExecutionFailureKind = TaskExecutionFailureKind.Other;
                accepted = false;
            }
        }
        if (!accepted) task.LastExecutionFailureKind = TaskExecutionFailureKind.Other;
        Log.Message($"[RimInfluence] capability execution id={task.CapabilityId} job={job.def?.defName} target={job.targetA.Thing?.LabelShort ?? job.targetA.Cell.ToString()} accepted={accepted}");
        return accepted;
    }
}
