using System;
using System.Linq;
using RimWorld;
using Verse;

namespace RimInfluence;

public enum TaskExecutionMode { Once, RepeatUntil }

public enum TaskStopConditionType { None, NoMoreTargets, NeedLevel, HealthPercent, ElapsedTime, IterationCount }

public enum TaskExecutionFailureKind { None, NoExecutableTarget, Other }

public enum TaskStopComparison { AtOrBelow, AtOrAbove }

internal static class TaskStopConditionEvaluator
{
    public static bool TryEvaluate(ScheduledTask task, out bool met, out string reason, out string error)
    {
        met = false;
        reason = "";
        error = "";
        if (task == null || task.ExecutionMode != TaskExecutionMode.RepeatUntil) return true;
        if (task.Pawn == null)
        {
            error = "持续任务无法读取角色状态。";
            return false;
        }

        switch (task.StopConditionType)
        {
            case TaskStopConditionType.NoMoreTargets:
                // This condition is resolved when the executor performs a fresh native scan.
                return true;

            case TaskStopConditionType.NeedLevel:
                Need need = task.Pawn.needs?.AllNeeds?.FirstOrDefault(n => n?.def != null
                    && string.Equals(n.def.defName, task.StopConditionDefName, StringComparison.OrdinalIgnoreCase));
                if (need == null)
                {
                    error = $"角色没有可读取的需求：{task.StopConditionDefName}。";
                    return false;
                }
                met = Compare(need.CurLevelPercentage, task.StopThreshold, task.StopComparison);
                reason = $"{need.LabelCap}={need.CurLevelPercentage:P0}, threshold={task.StopComparison} {task.StopThreshold:P0}";
                return true;

            case TaskStopConditionType.HealthPercent:
                float health = task.Pawn.health?.summaryHealth?.SummaryHealthPercent ?? 0f;
                met = Compare(health, task.StopThreshold, task.StopComparison);
                reason = $"health={health:P0}, threshold={task.StopComparison} {task.StopThreshold:P0}";
                return true;

            case TaskStopConditionType.ElapsedTime:
                if (task.StopAtTick < 0)
                {
                    error = "持续任务缺少有效的停止时间。";
                    return false;
                }
                met = GenTicks.TicksGame >= task.StopAtTick;
                reason = $"now={GenTicks.TicksGame}, stopAt={task.StopAtTick}";
                return true;

            case TaskStopConditionType.IterationCount:
                if (task.StopAmount <= 0)
                {
                    error = "持续任务的完成次数必须大于零。";
                    return false;
                }
                met = task.CompletedIterations >= task.StopAmount;
                reason = $"iterations={task.CompletedIterations}/{task.StopAmount}";
                return true;

            default:
                error = "持续任务没有可验证的停止条件。";
                return false;
        }
    }

    private static bool Compare(float current, float threshold, TaskStopComparison comparison)
    {
        return comparison == TaskStopComparison.AtOrAbove ? current >= threshold : current <= threshold;
    }
}
