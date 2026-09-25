using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimInfluence;

internal static class InfluenceActionExecutor
{
    public static bool SupportsLegacy(InfluenceAction action) => action switch
    {
        InfluenceAction.ChopWood or InfluenceAction.Harvest or InfluenceAction.Haul or InfluenceAction.Clean
            or InfluenceAction.Construct or InfluenceAction.Repair or InfluenceAction.Hunt or InfluenceAction.Mine
            or InfluenceAction.Sow or InfluenceAction.Butcher or InfluenceAction.Cook or InfluenceAction.Craft
            or InfluenceAction.Research or InfluenceAction.Doctor or InfluenceAction.Rescue or InfluenceAction.Extinguish
            or InfluenceAction.Tame or InfluenceAction.Train or InfluenceAction.Feed or InfluenceAction.Equip => true,
        _ => false
    };

    public static bool TryStart(ScheduledTask task)
    {
        if (task?.Pawn == null || task.Pawn.Downed)
        {
            if (task != null) Other(task, "角色当前倒地或不可执行任务。");
            return false;
        }
        if (NativeInteractionExecutor.Handles(task.CapabilityId)) return NativeInteractionExecutor.TryStart(task);
        if (string.Equals(task.CapabilityId, "command:StandStill", StringComparison.OrdinalIgnoreCase)) return TryStandStill(task);
        if (DynamicCapabilityExecutor.Handles(task.CapabilityId)) return DynamicCapabilityExecutor.TryStart(task);
        if (!string.IsNullOrEmpty(task.CapabilityId) && task.CapabilityId.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse(task.CapabilityId.Substring(7), true, out InfluenceAction legacyAction)) task.Action = legacyAction;
        if (!string.IsNullOrEmpty(task.JobDefName)) return TryGenericJob(task);
        if (task.Action == InfluenceAction.ChopWood) return TryChopWood(task);
        if (NativeActionResolver.TryExecute(task)) return true;
        bool started = task.Action switch
        {
            InfluenceAction.Haul => TryHaul(task),
            InfluenceAction.Harvest => TryHarvest(task),
            InfluenceAction.ChopWood => TryChopWood(task),
            InfluenceAction.Clean => TryClean(task),
            InfluenceAction.Equip => TryEquip(task),
            InfluenceAction.Construct => NoNativeOption(task),
            InfluenceAction.Repair => NoNativeOption(task),
            InfluenceAction.Hunt => NoNativeOption(task),
            InfluenceAction.Mine => NoNativeOption(task),
            InfluenceAction.Plant => NoNativeOption(task),
            InfluenceAction.Sow => NoNativeOption(task),
            InfluenceAction.Butcher => NoNativeOption(task),
            InfluenceAction.Cook => TryBill(task),
            InfluenceAction.Craft => TryBill(task),
            InfluenceAction.Research => NoNativeOption(task),
            InfluenceAction.Doctor => NoNativeOption(task),
            InfluenceAction.Rescue => NoNativeOption(task),
            InfluenceAction.Extinguish => NoNativeOption(task),
            InfluenceAction.Tame => NoNativeOption(task),
            InfluenceAction.Train => NoNativeOption(task),
            InfluenceAction.Feed => NoNativeOption(task),
            _ => Unsupported(task)
        };
        if (!started && task.LastExecutionFailureKind == TaskExecutionFailureKind.None)
            task.LastExecutionFailureKind = TaskExecutionFailureKind.Other;
        return started;
    }

    private static bool Unsupported(ScheduledTask task)
    {
        Log.Warning($"[RimInfluence] no explicit executor registered for action={task.Action}; WorkGiver fallback is disabled");
        return Other(task, "没有可用于该能力的执行器。");
    }

    private static bool TryStandStill(ScheduledTask task)
    {
        if (JobDefOf.Wait == null) return Other(task, "游戏没有原地等待任务。");
        Job job = JobMaker.MakeJob(JobDefOf.Wait);
        if (task.ExecutionMode == TaskExecutionMode.RepeatUntil
            && task.StopConditionType == TaskStopConditionType.ElapsedTime)
        {
            int remaining = task.StopAtTick - GenTicks.TicksGame;
            job.expiryInterval = Math.Max(1, Math.Min(remaining, GenDate.TicksPerHour));
        }
        else job.expiryInterval = -1;
        job.playerForced = true;
        return Take(task, Mark(task, job, CapabilityCatalog.Label(task.CapabilityId, task.Action)));
    }

    private static bool NoNativeOption(ScheduledTask task) => NoTarget(task,
        "游戏当前没有为该动作和目标提供可执行的原生交互；请使用对应的 WorkGiver 能力。");

    private static bool TryGenericJob(ScheduledTask task)
    {
        JobDef def = DefDatabase<JobDef>.GetNamedSilentFail(task.JobDefName);
        if (def == null) { Log.Warning($"[RimInfluence] JobDef not found: {task.JobDefName}"); return Other(task, "JobDef 不存在：" + task.JobDefName); }
        def = ResolveDirectJobDef(def);
        Thing target = FindTarget(task);
        def = ResolveCompatibleJobDef(def, target);
        if (RequiresThingTarget(def) && target == null)
        {
            Log.Warning($"[RimInfluence] required target not found for JobDef={def.defName}, query='{task.TargetQuery}', def='{task.TargetDefName}'");
            return NoTarget(task, "没有找到符合条件的执行目标。");
        }
        Job job = target == null ? JobMaker.MakeJob(def) : JobMaker.MakeJob(def, target);
        if (job == null) return Other(task, "游戏未能构造任务。");
        string report = RimInfluenceText.JobReport(def, target);
        bool taken = Take(task, Mark(task, job, report));
        Log.Message($"[RimInfluence] generic JobDef execution: requested={task.JobDefName}, actual={def.defName}, target={target?.LabelShort ?? "none"}, accepted={taken}, curJob={task.Pawn.jobs?.curJob?.def?.defName ?? "none"}");
        return taken;
    }

    private static JobDef ResolveCompatibleJobDef(JobDef selected, Thing target)
    {
        if (selected == null || !(target is Plant plant)) return selected;
        if (plant.def?.plant?.IsTree == true
            && (string.Equals(selected.defName, "Harvest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(selected.defName, "HarvestDesignated", StringComparison.OrdinalIgnoreCase)))
        {
            JobDef cut = DefDatabase<JobDef>.GetNamedSilentFail("CutPlant");
            if (cut != null)
            {
                Log.Message($"[RimInfluence] normalized incompatible JobDef {selected.defName} -> {cut.defName} for tree target {plant.LabelShort}");
                return cut;
            }
        }
        return selected;
    }

    private static bool RequiresThingTarget(JobDef def)
    {
        if (def == null) return false;
        string name = def.defName ?? "";
        if (name.Equals("CutPlant", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Harvest", StringComparison.OrdinalIgnoreCase)
            || name.Equals("HarvestDesignated", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Clean", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Equip", StringComparison.OrdinalIgnoreCase)) return true;
        Type driver = def.driverClass;
        return driver != null && (driver.Name.IndexOf("Plant", StringComparison.OrdinalIgnoreCase) >= 0
            || driver.Name.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0
            || driver.Name.IndexOf("Clean", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static JobDef ResolveDirectJobDef(JobDef selected)
    {
        Type driver = selected.driverClass;
        if (driver == null || driver.Name.IndexOf("Designated", StringComparison.OrdinalIgnoreCase) < 0) return selected;
        JobDef direct = DefDatabase<JobDef>.AllDefsListForReading
            .Where(d => d != null && d != selected && d.driverClass != null && driver.IsSubclassOf(d.driverClass))
            .OrderByDescending(d => InheritanceDepth(d.driverClass))
            .FirstOrDefault();
        if (direct == null) return selected;
        Log.Message($"[RimInfluence] normalized designation-gated JobDef {selected.defName} -> {direct.defName}");
        return direct;
    }

    private static int InheritanceDepth(Type type)
    {
        int depth = 0;
        while (type != null) { depth++; type = type.BaseType; }
        return depth;
    }

    private static bool TryHaul(ScheduledTask task)
    {
        ThingDef def = string.IsNullOrEmpty(task.TargetDefName) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(task.TargetDefName);
        Thing thing = task.Pawn.Map.listerThings.AllThings.Where(t => t.Spawned && t.def.EverHaulable && (def == null || t.def == def))
            .Where(t => !t.IsForbidden(task.Pawn) && task.Pawn.CanReach(t, PathEndMode.Touch, Danger.Some) && task.Pawn.CanReserve(t))
            .Where(t => MatchesQuery(t, task.TargetQuery))
            .OrderByDescending(t => t.stackCount).ThenBy(t => task.Pawn.Position.DistanceToSquared(t.Position)).FirstOrDefault();
        if (thing == null || !StoreUtility.TryFindBestBetterStoreCellFor(thing, task.Pawn, task.Pawn.Map, StoragePriority.Unstored, task.Pawn.Faction, out IntVec3 cell))
            return NoTarget(task, "没有剩余可搬运并具有有效储存位置的目标。");
        Job job = JobMaker.MakeJob(JobDefOf.HaulToCell, thing, cell);
        job.count = Math.Min(thing.stackCount, MassUtility.CountToPickUpUntilOverEncumbered(task.Pawn, thing));
        return Take(task, Mark(task, job, RimInfluenceText.JobReport(task.Action, thing)));
    }

    private static bool TryHarvest(ScheduledTask task)
    {
        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(task.TargetDefName);
        Plant plant = task.Pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>()
            .Where(p => p.Spawned && p.HarvestableNow && !p.IsForbidden(task.Pawn) && task.Pawn.CanReach(p, PathEndMode.Touch, Danger.Some) && task.Pawn.CanReserve(p))
            .Where(p => def == null || p.def == def)
            .Where(p => MatchesQuery(p, task.TargetQuery))
            .OrderBy(p => task.Pawn.Position.DistanceToSquared(p.Position)).FirstOrDefault();
        if (plant == null) return NoTarget(task, "没有剩余可收获目标。");
        return Take(task, Mark(task, JobMaker.MakeJob(JobDefOf.Harvest, plant), RimInfluenceText.JobReport(task.Action, plant)));
    }

    private static bool TryChopWood(ScheduledTask task)
    {
        ThingDef requested = DefDatabase<ThingDef>.GetNamedSilentFail(task.TargetDefName);
        Plant tree = task.Pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>()
            .Where(p => p.Spawned && p.def?.plant?.IsTree == true && !p.def.plant.isStump && !p.IsForbidden(task.Pawn))
            .Where(p => task.Pawn.CanReach(p, PathEndMode.Touch, Danger.Some) && task.Pawn.CanReserve(p))
            .Where(p => requested == null || p.def == requested)
            .Where(p => MatchesQuery(p, task.TargetQuery))
            .OrderBy(p => task.Pawn.Position.DistanceToSquared(p.Position)).FirstOrDefault();
        if (tree == null) return NoTarget(task, "没有剩余可砍伐目标。");
        return Take(task, Mark(task, JobMaker.MakeJob(JobDefOf.CutPlant, tree), RimInfluenceText.JobReport(task.Action, tree)));
    }

    private static bool TryClean(ScheduledTask task)
    {
        Filth filth = task.Pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).OfType<Filth>()
            .Where(f => task.Pawn.CanReach(f, PathEndMode.Touch, Danger.Some) && task.Pawn.CanReserve(f)).FirstOrDefault();
        if (filth == null) return NoTarget(task, "没有剩余可清扫目标。");
        return Take(task, Mark(task, JobMaker.MakeJob(JobDefOf.Clean, filth), RimInfluenceText.JobReport(task.Action, filth)));
    }

    private static bool TryEquip(ScheduledTask task)
    {
        if (task.Pawn.equipment?.Primary != null)
        {
            task.LastExecutionError = "角色已经装备了主要武器。";
            return false;
        }
        if (task.Pawn.WorkTagIsDisabled(WorkTags.Violent))
        {
            task.LastExecutionError = "角色没有暴力能力，不能装备或使用武器。";
            return false;
        }
        Thing weapon = task.Pawn.Map.listerThings.AllThings.Where(t => t.Spawned && t.def.IsWeapon && !t.IsForbidden(task.Pawn) && task.Pawn.CanReach(t, PathEndMode.Touch, Danger.Some) && task.Pawn.CanReserve(t))
            .Where(t => string.IsNullOrEmpty(task.TargetDefName) || t.def.defName == task.TargetDefName)
            .Where(t => MatchesQuery(t, task.TargetQuery)).FirstOrDefault();
        if (weapon == null)
        {
            task.LastExecutionError = "没有找到角色能够到达且符合条件的武器。";
            task.LastExecutionFailureKind = TaskExecutionFailureKind.NoExecutableTarget;
            return false;
        }
        JobDef equip = DefDatabase<JobDef>.GetNamedSilentFail("Equip");
        if (equip == null)
        {
            task.LastExecutionError = "游戏没有加载 Equip 任务定义。";
            return false;
        }
        if (!EquipmentUtility.CanEquip(weapon, task.Pawn))
        {
            task.LastExecutionError = "角色当前不能装备这件武器，可能是武器类型、健康状态或装备规则不允许。";
            return false;
        }
        return Take(task, Mark(task, JobMaker.MakeJob(equip, weapon), RimInfluenceText.JobReport(task.Action, weapon)));
    }

    private static bool TryBill(ScheduledTask task)
    {
        if (string.IsNullOrEmpty(task.RecipeDefName))
        {
            Log.Warning($"[RimInfluence] {task.Action} requires recipeDefName");
            return Other(task, task.Action + " 缺少 recipeDefName。");
        }
        RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(task.RecipeDefName);
        if (recipe == null) { Log.Warning($"[RimInfluence] recipe not found: {task.RecipeDefName}"); return Other(task, "配方不存在：" + task.RecipeDefName); }
        Thing bench = FindTarget(task);
        JobDef doBill = DefDatabase<JobDef>.GetNamedSilentFail("DoBill");
        if (bench == null) return NoTarget(task, "没有剩余符合条件的工作台。");
        if (doBill == null) return Other(task, "游戏没有加载 DoBill 任务定义。");
        Job job = JobMaker.MakeJob(doBill, bench);
        job.bill = new Bill_Production(recipe);
        return Take(task, Mark(task, job, RimInfluenceText.JobReport(task.Action, bench)));
    }

    private static Thing FindTarget(ScheduledTask task)
    {
        IEnumerable<Thing> candidates = task.Pawn.Map.listerThings.AllThings.Where(t => t.Spawned && !t.IsForbidden(task.Pawn) && task.Pawn.CanReach(t, PathEndMode.Touch, Danger.Some) && task.Pawn.CanReserve(t));
        if (string.Equals(task.JobDefName, "CutPlant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(task.JobDefName, "CutPlantDesignated", StringComparison.OrdinalIgnoreCase))
            candidates = task.Pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).Where(t => t.Spawned && !t.IsForbidden(task.Pawn) && task.Pawn.CanReach(t, PathEndMode.Touch, Danger.Some) && task.Pawn.CanReserve(t));
        return candidates
            .Where(t => string.IsNullOrEmpty(task.TargetDefName) || t.def.defName == task.TargetDefName)
            .Where(t => string.IsNullOrEmpty(task.TargetQuery)
                || t.def.defName.IndexOf(task.TargetQuery, StringComparison.OrdinalIgnoreCase) >= 0
                || t.LabelCap.ToString().IndexOf(task.TargetQuery, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(t => task.Pawn.Position.DistanceToSquared(t.Position)).FirstOrDefault();
    }

    private static bool MatchesQuery(Thing thing, string query)
    {
        if (thing == null || string.IsNullOrWhiteSpace(query)) return true;
        if (thing.def.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || thing.LabelCap.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (thing is Plant plant && plant.def?.plant?.IsTree == true
            && (query.Equals("tree", StringComparison.OrdinalIgnoreCase) || query.IndexOf("树", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
        return false;
    }

    private static bool Take(ScheduledTask task, Job job)
    {
        JobDef expectedDef = job?.def;
        if (job == null || !task.Pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)) return false;
        Job current = task.Pawn.jobs.curJob;
        if (job.def == expectedDef && current != null && current.JobIsSameAs(task.Pawn, job)) task.ActiveJob = current;
        else if (job.def == expectedDef && task.Pawn.jobs.jobQueue.Contains(job)) task.ActiveJob = job;
        else
        {
            task.LastExecutionError = "游戏接受派发后立即丢弃了原生 Job，未进入执行或等待队列。";
            task.LastExecutionFailureKind = TaskExecutionFailureKind.Other;
            return false;
        }
        return true;
    }

    private static bool NoTarget(ScheduledTask task, string reason)
    {
        task.LastExecutionFailureKind = TaskExecutionFailureKind.NoExecutableTarget;
        task.LastExecutionError = reason;
        return false;
    }

    private static bool Other(ScheduledTask task, string reason)
    {
        task.LastExecutionFailureKind = TaskExecutionFailureKind.Other;
        task.LastExecutionError = reason;
        return false;
    }

    private static Job Mark(ScheduledTask task, Job job, string label)
    {
        if (job != null && RimInfluenceMod.Settings.MarkTriggeredJobs)
            job.reportStringOverride = label;
        return job;
    }
}
