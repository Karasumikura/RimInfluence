using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimInfluence;

public sealed class RimInfluenceWorldComponent : WorldComponent
{
    public List<ScheduledTask> Tasks = new();
    private int _lastCheckTick;

    public RimInfluenceWorldComponent(World world) : base(world) { }

    public override void ExposeData() {
        base.ExposeData();
        Scribe_Collections.Look(ref Tasks, "rimInfluenceTasks", LookMode.Deep);
        if (Scribe.mode == LoadSaveMode.PostLoadInit) Tasks ??= new List<ScheduledTask>();
    }

    public override void WorldComponentTick()
    {
        base.WorldComponentTick();
        if (CapabilityAutoTest.Tick(world)) return;
        for (int i = 0; i < Tasks.Count; i++)
        {
            ScheduledTask task = Tasks[i];
            if (task == null || task.Status is not (ScheduledTaskStatus.Pending or ScheduledTaskStatus.WaitingForRetry)) continue;
            if (GenTicks.TicksGame >= task.ExecuteAtTick) TryExecute(task);
        }

        if (GenTicks.TicksGame - _lastCheckTick < 250) return;
        _lastCheckTick = GenTicks.TicksGame;
        CapabilityAudit.EnsureInitialized(world, Tasks);
        RimTalkIntegration.Drain();
        DrainFailureDialogues();
        foreach (var task in Tasks.Where(t => t != null && t.Status == ScheduledTaskStatus.Running).ToList())
        {
            if (task.Pawn == null || task.Pawn.DestroyedOrNull()) { Fail(task, RimInfluenceText.PawnUnavailable(true)); continue; }
            if (task.StartedTick < 0)
            {
                if (task.Pawn.jobs?.curJob == task.ActiveJob)
                {
                    task.StartedTick = GenTicks.TicksGame;
                    Log.Message($"[RimInfluence] queued job now active id={task.Id} job={task.ActiveJob?.def?.defName}");
                }
                else if (task.Pawn.jobs?.jobQueue?.Contains(task.ActiveJob) == false)
                    Fail(task, "游戏接受了任务，但原生 Job 在开始前从等待队列消失。");
                else if (GenTicks.TicksGame - task.LastAttemptTick > 5000)
                    Fail(task, "游戏接受了任务队列，但该原生 Job 始终未开始。");
                continue;
            }
            if (!CheckStopCondition(task, true)) continue;
            LogRunningState(task);
            if (task.StartedTick >= 0 && GenTicks.TicksGame - task.StartedTick > 500 && task.Pawn.jobs?.curJob != task.ActiveJob)
                Fail(task, "任务已结束，但游戏没有返回可确认的成功状态。");
        }
    }

    public ScheduledTask AddHarvestPlan(Pawn pawn, int executeAtTick, string source)
    {
        var task = AddPlan(pawn, InfluenceAction.Harvest, "Plant_Corn", executeAtTick, source);
        return task;
    }

    public ScheduledTask AddPlan(Pawn pawn, InfluenceAction action, string targetDefName, int executeAtTick, string source)
    {
        var task = new ScheduledTask { Id = Guid.NewGuid().ToString("N"), Pawn = pawn, Action = action, TargetDefName = targetDefName, ExecuteAtTick = executeAtTick, SourceDialogue = source };
        Tasks.Add(task);
        return task;
    }

    public int CancelTasks(Pawn pawn, string capabilityId, string targetQuery)
    {
        int count = 0;
        foreach (ScheduledTask task in Tasks.Where(t => t != null && t.Pawn == pawn
                     && (t.Status == ScheduledTaskStatus.Pending || t.Status == ScheduledTaskStatus.WaitingForRetry || t.Status == ScheduledTaskStatus.Running)
                     && (string.IsNullOrWhiteSpace(capabilityId) || string.Equals(t.CapabilityId, capabilityId, StringComparison.OrdinalIgnoreCase))
                     && (string.IsNullOrWhiteSpace(targetQuery) || string.Equals(t.TargetQuery, targetQuery, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            task.Status = ScheduledTaskStatus.Cancelled;
            task.FailureDialoguePending = false;
            NativeInteractionExecutor.RestoreDraft(task);
            if (task.ActiveJob != null)
                task.Pawn.jobs?.jobQueue?.RemoveAll(task.Pawn, job => ReferenceEquals(job, task.ActiveJob));
            if (task.ActiveJob != null && task.Pawn.jobs?.curJob == task.ActiveJob)
                task.Pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            Log.Message($"[RimInfluence] task cancelled id={task.Id} pawn={pawn.LabelShort} capability={task.CapabilityId}");
            count++;
        }
        return count;
    }

    private void TryExecute(ScheduledTask task)
    {
        task.LastAttemptTick = GenTicks.TicksGame;
        task.LastExecutionError = "";
        task.LastExecutionFailureKind = TaskExecutionFailureKind.None;
        if (task.Pawn == null || task.Pawn.DestroyedOrNull() || task.Pawn.Map == null) { Fail(task, RimInfluenceText.PawnUnavailable(false)); return; }
        if (!CheckStopCondition(task, false)) return;
        if (Tasks.Any(running => running != task && running != null
            && running.Status == ScheduledTaskStatus.Running && running.Pawn == task.Pawn
            && string.Equals(running.CapabilityId, "command:StandStill", StringComparison.OrdinalIgnoreCase)
            && running.ActiveJob == task.Pawn.jobs?.curJob) || !IsAvailable(task.Pawn))
        {
            if (GenTicks.TicksGame >= task.NextDiagnosticTick)
            {
                task.NextDiagnosticTick = GenTicks.TicksGame + 1000;
                Log.Message($"[RimInfluence] due task waiting for idle pawn id={task.Id} pawn={task.Pawn.LabelShort} currentJob={task.Pawn.jobs?.curJob?.def?.defName}");
            }
            return;
        }
        bool started;
        try { started = InfluenceActionExecutor.TryStart(task); }
        catch (Exception ex)
        {
            Fail(task, "执行器异常：" + ex.GetBaseException().Message);
            Log.Warning($"[RimInfluence] executor exception id={task.Id}: {ex}");
            return;
        }
        if (started)
        {
            Job active = task.ActiveJob ?? task.Pawn.jobs?.curJob;
            if (active == null)
            {
                Fail(task, "执行器报告已派发，但角色没有接到原生 Job。");
                return;
            }
            task.Status = ScheduledTaskStatus.Running;
            task.StartedTick = task.Pawn.jobs?.curJob == active ? GenTicks.TicksGame : -1;
            task.NextDiagnosticTick = GenTicks.TicksGame;
            task.ActiveJob = active;
            task.LastJobDefName = active.def?.defName ?? "";
            Log.Message($"[RimInfluence] job dispatched id={task.Id} job={task.LastJobDefName} active={task.StartedTick >= 0} current={task.Pawn.jobs?.curJob?.def?.defName ?? "none"}");
            CapabilityAudit.Refresh(Tasks);
            return;
        }
        if (task.ExecutionMode == TaskExecutionMode.RepeatUntil
            && task.StopConditionType == TaskStopConditionType.NoMoreTargets
            && task.LastExecutionFailureKind == TaskExecutionFailureKind.NoExecutableTarget)
        {
            Complete(task, "stop condition met: no executable targets remain");
            return;
        }
        Fail(task, string.IsNullOrWhiteSpace(task.LastExecutionError)
            ? RimInfluenceText.NoExecutableTarget(task.Action, task.TargetDefName)
            : task.LastExecutionError);
    }

    private static bool IsAvailable(Pawn pawn)
    {
        Job job = pawn?.jobs?.curJob;
        if (job == null) return true;
        string name = job.def?.defName ?? "";
        return name == "Wait_Wander" || name == "GotoWander" || name == "Wait_MaintainPosture" || name == "Wait" || name == "Wait_Combat";
    }

    public void NotifyJobEnded(Pawn pawn, Job job, JobCondition condition)
    {
        ScheduledTask task = Tasks.FirstOrDefault(t => t != null && t.Status == ScheduledTaskStatus.Running
            && t.Pawn == pawn && ReferenceEquals(t.ActiveJob, job));
        if (task == null) return;
        NativeInteractionExecutor.RestoreDraft(task);
        Log.Message($"[RimInfluence] tracked job ended id={task.Id} job={job.def?.defName} condition={condition} target={job.targetA.Thing?.LabelShort ?? job.targetA.Cell.ToString()} targetExists={job.targetA.Thing?.Spawned ?? false}");
        if (condition == JobCondition.Succeeded)
        {
            if (string.Equals(task.CapabilityId, "interaction:Arrest", StringComparison.OrdinalIgnoreCase)
                && job.targetA.Pawn?.IsPrisonerOfColony != true)
            {
                Fail(task, "拘捕未成功：目标没有成为殖民地囚犯，可能拒绝了逮捕。");
                return;
            }
            task.CompletedIterations++;
            CapabilityAudit.Refresh(Tasks);
            task.ConsecutiveRecoveryFailures = 0;
            if (!CheckStopCondition(task, false)) return;
            if (task.ExecutionMode == TaskExecutionMode.RepeatUntil)
            {
                task.Status = ScheduledTaskStatus.Pending;
                task.ExecuteAtTick = GenTicks.TicksGame + 1;
                task.StartedTick = -1;
                task.ActiveJob = null;
                Log.Message($"[RimInfluence] repeated task continuing id={task.Id} iteration={task.CompletedIterations}");
            }
            else Complete(task, "native job succeeded");
        }
        else if ((condition == JobCondition.InterruptForced || condition == JobCondition.Incompletable)
            && task.ExecutionMode == TaskExecutionMode.RepeatUntil)
        {
            task.ConsecutiveRecoveryFailures++;
            if (task.ConsecutiveRecoveryFailures > 3)
            {
                Fail(task, "连续多次原生任务无法完成（目标可能持续被其他工作占用或执行条件不满足）。");
                return;
            }
            task.Status = ScheduledTaskStatus.Pending;
            task.ExecuteAtTick = GenTicks.TicksGame + 60;
            task.StartedTick = -1;
            task.ActiveJob = null;
            Log.Message($"[RimInfluence] repeated task recovering after native {condition} id={task.Id}; recovery={task.ConsecutiveRecoveryFailures}/3; rescanning targets");
        }
        else Fail(task, "游戏返回任务结束状态：" + condition + "。");
    }

    private bool CheckStopCondition(ScheduledTask task, bool interruptRunning)
    {
        if (!TaskStopConditionEvaluator.TryEvaluate(task, out bool met, out string reason, out string error))
        {
            Fail(task, error);
            return false;
        }
        if (!met) return true;
        Job activeJob = task.ActiveJob;
        Complete(task, "stop condition met: " + reason);
        if (interruptRunning && activeJob != null && task.Pawn?.jobs?.curJob == activeJob)
            task.Pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
        return false;
    }

    private static void LogRunningState(ScheduledTask task)
    {
        if (GenTicks.TicksGame < task.NextDiagnosticTick) return;
        task.NextDiagnosticTick = GenTicks.TicksGame + 1000;
        Job job = task.Pawn.jobs?.curJob;
        Thing target = job?.targetA.Thing;
        string toil = "none";
        try
        {
            object curToil = task.Pawn.jobs?.curDriver?.GetType().GetProperty("CurToil")?.GetValue(task.Pawn.jobs.curDriver);
            toil = curToil?.GetType().GetField("debugName")?.GetValue(curToil)?.ToString() ?? curToil?.ToString() ?? "none";
        }
        catch { }
        float plantSpeed = 0f;
        try { plantSpeed = task.Pawn.GetStatValue(StatDefOf.PlantWorkSpeed); } catch { }
        Log.Message($"[RimInfluence] running id={task.Id} job={job?.def?.defName ?? "none"} pawnPos={task.Pawn.Position} target={target?.LabelShort ?? "none"} targetPos={target?.Position.ToString() ?? "none"} moving={task.Pawn.pather?.MovingNow ?? false} toil={toil} plantSpeed={plantSpeed:0.###}");
    }

    private void Fail(ScheduledTask task, string reason)
    {
        NativeInteractionExecutor.RestoreDraft(task);
        if (task.StartedTick < 0 && task.ActiveJob != null && task.Pawn?.jobs != null)
            task.Pawn.jobs.jobQueue.RemoveAll(task.Pawn, job => ReferenceEquals(job, task.ActiveJob));
        task.Status = ScheduledTaskStatus.Failed;
        task.FailureReason = reason;
        Log.Warning($"[RimInfluence] task failed id={task.Id} pawn={task.Pawn?.LabelShort} action={task.Action}: {reason}");
        CapabilityAudit.Refresh(Tasks);
        TaskThoughts.AddFailure(task.Pawn);
        Messages.Message(RimInfluenceText.Failed(task, reason), MessageTypeDefOf.RejectInput);
        if (RimInfluenceMod.Settings.ReportFailureDialogue || RimInfluenceMod.Settings.EnableMultiTurn)
        {
            task.FailureDialoguePending = true;
            Log.Message($"[RimInfluence] failure dialogue queued id={task.Id}; waiting for RimTalk availability");
        }
    }

    private void DrainFailureDialogues()
    {
        if (!RimInfluenceMod.Settings.ReportFailureDialogue && !RimInfluenceMod.Settings.EnableMultiTurn) return;
        foreach (var task in Tasks.Where(t => t != null && t.FailureDialoguePending).ToList())
        {
            if (task.Pawn == null || task.Pawn.DestroyedOrNull() || task.Pawn.Dead)
            {
                task.FailureDialoguePending = false;
                Log.Warning($"[RimInfluence] failure dialogue discarded id={task.Id}: speaker unavailable");
                continue;
            }
            if (task.Pawn.Map == null) continue;
            bool continueAgent = RimInfluenceMod.Settings.EnableMultiTurn
                && task.ContinuationDepth < RimInfluenceMod.Settings.MaxContinuationRounds;
            if (!continueAgent && (task.Status == ScheduledTaskStatus.Completed || !RimInfluenceMod.Settings.ReportFailureDialogue))
            {
                task.FailureDialoguePending = false;
                continue;
            }
            if (continueAgent
                ? RimTalkBridge.TryContinue(task.Pawn, task)
                : RimTalkBridge.TryReportFailure(task.Pawn, task.SourceDialogue, task.FailureReason))
            {
                task.FailureDialoguePending = false;
                Log.Message($"[RimInfluence] failure dialogue dequeued id={task.Id}: RimTalk accepted");
                break; // Submit at most one request per check, including while AIService starts asynchronously.
            }
        }
    }

    private void Complete(ScheduledTask task, string reason)
    {
        NativeInteractionExecutor.RestoreDraft(task);
        task.Status = ScheduledTaskStatus.Completed;
        task.ActiveJob = null;
        Log.Message($"[RimInfluence] task completed id={task.Id} pawn={task.Pawn.LabelShort} action={task.Action} iterations={task.CompletedIterations} reason={reason}");
        CapabilityAudit.Refresh(Tasks);
        TaskThoughts.AddSuccess(task.Pawn);
        Messages.Message(RimInfluenceText.Completed(task), MessageTypeDefOf.PositiveEvent);
        if (RimInfluenceMod.Settings.EnableMultiTurn && task.ContinuationDepth < RimInfluenceMod.Settings.MaxContinuationRounds)
            task.FailureDialoguePending = true;
    }
}
