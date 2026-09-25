using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimInfluence;

internal static class NativeInteractionExecutor
{
    private static readonly MethodInfo ArrestOption = typeof(FloatMenuOptionProvider_Arrest)
        .GetMethod("GetSingleOptionFor", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(Pawn), typeof(FloatMenuContext) }, null);

    public static bool Available => ArrestOption != null;

    public static bool Handles(string id) => string.Equals(id, "interaction:Arrest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, "interaction:Attack", StringComparison.OrdinalIgnoreCase);

    public static bool TryStart(ScheduledTask task)
    {
        if (string.Equals(task.CapabilityId, "interaction:Attack", StringComparison.OrdinalIgnoreCase))
            return TryAttack(task);
        string name = task.TargetQuery?.Trim() ?? "";
        if (name.Length == 0)
        {
            task.LastExecutionError = "拘捕任务没有指定目标角色。";
            return false;
        }
        Pawn[] targets = task.Pawn.Map.mapPawns.AllPawnsSpawned
            .Where(p => p != null && p != task.Pawn && !p.Dead
                && (string.Equals(p.LabelShort, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.LabelCap.ToString(), name, StringComparison.OrdinalIgnoreCase)))
            .Take(2).ToArray();
        if (targets.Length != 1)
        {
            task.LastExecutionError = targets.Length == 0 ? "没有找到指定的拘捕目标：" + name : "拘捕目标姓名不唯一：" + name;
            task.LastExecutionFailureKind = TaskExecutionFailureKind.NoExecutableTarget;
            return false;
        }
        Pawn target = targets[0];
        try
        {
            Vector3 click = target.Position.ToVector3Shifted();
            var context = new FloatMenuContext(new List<Pawn> { task.Pawn }, click, task.Pawn.Map);
            var provider = new FloatMenuOptionProvider_Arrest();
            FloatMenuOption option = ArrestOption?.Invoke(provider, new object[] { target, context }) as FloatMenuOption;
            if (option?.action == null || option.Disabled)
            {
                task.LastExecutionError = option?.Label ?? "游戏当前不允许该角色拘捕目标。";
                return false;
            }
            Building_Bed bed = RestUtility.FindBedFor(target, task.Pawn, false, false, GuestStatus.Prisoner)
                ?? RestUtility.FindBedFor(target, task.Pawn, false, true, GuestStatus.Prisoner);
            if (bed == null)
            {
                task.LastExecutionError = "没有可用的囚犯床。";
                task.LastExecutionFailureKind = TaskExecutionFailureKind.NoExecutableTarget;
                return false;
            }
            option.action();
            Job job = task.Pawn.jobs?.curJob;
            bool accepted = job?.def == JobDefOf.Arrest && job.targetA.Pawn == target;
            if (accepted) task.ActiveJob = job;
            if (accepted && RimInfluenceMod.Settings.MarkTriggeredJobs)
                job.reportStringOverride = RimInfluenceText.JobReport(job.def, target);
            if (!accepted) task.LastExecutionError = "游戏没有接受原生拘捕任务。";
            Log.Message($"[RimInfluence] native interaction id=interaction:Arrest pawn={task.Pawn.LabelShort} target={target.LabelShort} job={job?.def?.defName ?? "none"} accepted={accepted}");
            return accepted;
        }
        catch (Exception ex)
        {
            task.LastExecutionError = "原生拘捕交互失败：" + ex.GetBaseException().Message;
            Log.Warning("[RimInfluence] arrest interaction failed: " + ex);
            return false;
        }
    }

    private static bool TryAttack(ScheduledTask task)
    {
        string name = task.TargetQuery?.Trim() ?? "";
        if (name.Length == 0)
        {
            task.LastExecutionError = "攻击任务没有指定目标角色。";
            return false;
        }
        Pawn[] targets = task.Pawn.Map.mapPawns.AllPawnsSpawned
            .Where(p => p != null && p != task.Pawn && !p.Dead
                && (string.Equals(p.LabelShort, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.LabelCap.ToString(), name, StringComparison.OrdinalIgnoreCase)))
            .Take(2).ToArray();
        if (targets.Length != 1)
        {
            task.LastExecutionError = targets.Length == 0 ? "没有找到指定的攻击目标：" + name : "攻击目标姓名不唯一：" + name;
            task.LastExecutionFailureKind = TaskExecutionFailureKind.NoExecutableTarget;
            return false;
        }
        Pawn target = targets[0];
        if (task.Pawn.drafter == null)
        {
            task.LastExecutionError = "该角色不能接受征召攻击命令。";
            return false;
        }
        try
        {
            if (!task.Pawn.Drafted)
            {
                task.Pawn.drafter.Drafted = true;
                task.AutoDraftedForAttack = true;
            }
            Action action = FloatMenuUtility.GetAttackAction(task.Pawn, target, out string failure);
            if (action == null && FloatMenuUtility.UseRangedAttack(task.Pawn))
                action = FloatMenuUtility.GetMeleeAttackAction(task.Pawn, target, out failure);
            if (action == null)
            {
                task.LastExecutionError = string.IsNullOrWhiteSpace(failure) ? "游戏当前不允许攻击该目标。" : failure;
                RestoreDraft(task);
                return false;
            }
            action();
            Job job = task.Pawn.jobs?.curJob;
            if (job?.targetA.Pawn != target || (job.def != JobDefOf.AttackMelee && job.def != JobDefOf.AttackStatic))
                job = task.Pawn.jobs?.jobQueue?.Select(queued => queued.job)
                    .FirstOrDefault(queued => queued.targetA.Pawn == target
                        && (queued.def == JobDefOf.AttackMelee || queued.def == JobDefOf.AttackStatic));
            if (job == null)
            {
                task.LastExecutionError = "游戏没有接受原生攻击任务。";
                RestoreDraft(task);
                return false;
            }
            task.ActiveJob = job;
            if (RimInfluenceMod.Settings.MarkTriggeredJobs)
                job.reportStringOverride = RimInfluenceText.JobReport(job.def, target);
            Log.Message($"[RimInfluence] native attack pawn={task.Pawn.LabelShort} target={target.LabelShort} job={job.def.defName}");
            return true;
        }
        catch (Exception ex)
        {
            RestoreDraft(task);
            task.LastExecutionError = "原生攻击交互失败：" + ex.GetBaseException().Message;
            Log.Warning("[RimInfluence] attack interaction failed: " + ex);
            return false;
        }
    }

    public static void RestoreDraft(ScheduledTask task)
    {
        if (!task.AutoDraftedForAttack) return;
        task.AutoDraftedForAttack = false;
        if (task.Pawn?.drafter != null && !task.Pawn.DestroyedOrNull())
            task.Pawn.drafter.Drafted = false;
    }
}
