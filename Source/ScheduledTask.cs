using Verse;
using Verse.AI;

namespace RimInfluence;

public enum ScheduledTaskStatus { Pending, Running, Completed, Failed, WaitingForRetry, Cancelled }

public enum InfluenceAction
{
    Harvest,
    Haul,
    Clean,
    Construct,
    Repair,
    Hunt,
    Mine,
    Plant,
    ChopWood,
    Sow,
    Butcher,
    Cook,
    Doctor,
    Rescue,
    Extinguish,
    Tame,
    Train,
    Feed,
    Equip,
    Craft,
    Research,
    Work,
    Custom
}

public sealed class ScheduledTask : IExposable
{
    public string Id = "";
    public Pawn Pawn;
    public InfluenceAction Action = InfluenceAction.Harvest;
    public string CapabilityId = "";
    public string JobDefName = "";
    public string TargetDefName = "Plant_Corn";
    public string TargetQuery = "";
    public string RecipeDefName = "";
    public string LastJobDefName = "";
    public int ExecuteAtTick;
    public int LastAttemptTick = -1;
    public ScheduledTaskStatus Status;
    public string FailureReason = "";
    public bool FailureDialoguePending;
    public int RetryCount;
    public int StartedTick = -1;
    public TaskExecutionMode ExecutionMode = TaskExecutionMode.Once;
    public TaskStopConditionType StopConditionType = TaskStopConditionType.None;
    public TaskStopComparison StopComparison = TaskStopComparison.AtOrBelow;
    public string StopConditionDefName = "";
    public float StopThreshold;
    public int StopAmount;
    public int StopAtTick = -1;
    public int CompletedIterations;
    public int ConsecutiveRecoveryFailures;
    [Unsaved] public int NextDiagnosticTick;
    [Unsaved] public Job ActiveJob;
    [Unsaved] public bool AutoDraftedForAttack;
    [Unsaved] public string LastExecutionError = "";
    [Unsaved] public TaskExecutionFailureKind LastExecutionFailureKind;
    public string SourceDialogue = "";

    public void ExposeData()
    {
        Scribe_Values.Look(ref Id, "id");
        Scribe_References.Look(ref Pawn, "pawn");
        Scribe_Values.Look(ref Action, "action", InfluenceAction.Harvest);
        Scribe_Values.Look(ref CapabilityId, "capabilityId", "");
        Scribe_Values.Look(ref JobDefName, "jobDefName", "");
        Scribe_Values.Look(ref TargetDefName, "targetDefName", "Plant_Corn");
        Scribe_Values.Look(ref TargetQuery, "targetQuery", "");
        Scribe_Values.Look(ref RecipeDefName, "recipeDefName", "");
        Scribe_Values.Look(ref LastJobDefName, "lastJobDefName", "");
        Scribe_Values.Look(ref ExecuteAtTick, "executeAtTick");
        Scribe_Values.Look(ref LastAttemptTick, "lastAttemptTick", -1);
        Scribe_Values.Look(ref Status, "status", ScheduledTaskStatus.Pending);
        Scribe_Values.Look(ref FailureReason, "failureReason", "");
        Scribe_Values.Look(ref FailureDialoguePending, "failureDialoguePending", false);
        Scribe_Values.Look(ref RetryCount, "retryCount");
        Scribe_Values.Look(ref StartedTick, "startedTick", -1);
        Scribe_Values.Look(ref ExecutionMode, "executionMode", TaskExecutionMode.Once);
        Scribe_Values.Look(ref StopConditionType, "stopConditionType", TaskStopConditionType.None);
        Scribe_Values.Look(ref StopComparison, "stopComparison", TaskStopComparison.AtOrBelow);
        Scribe_Values.Look(ref StopConditionDefName, "stopConditionDefName", "");
        Scribe_Values.Look(ref StopThreshold, "stopThreshold", 0f);
        Scribe_Values.Look(ref StopAmount, "stopAmount", 0);
        Scribe_Values.Look(ref StopAtTick, "stopAtTick", -1);
        Scribe_Values.Look(ref CompletedIterations, "completedIterations", 0);
        Scribe_Values.Look(ref ConsecutiveRecoveryFailures, "consecutiveRecoveryFailures", 0);
        Scribe_Values.Look(ref SourceDialogue, "sourceDialogue", "");
    }
}
