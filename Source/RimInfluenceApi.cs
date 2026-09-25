using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;

namespace RimInfluence;

/// <summary>Bridge for RimTalk adapters. Feed the structured JSON returned by an AI response here.</summary>
public static class RimInfluenceApi
{
    private static readonly Regex StringField = new Regex("\\\"(?<key>decision|actor|capabilityId|actionDescription|action|jobDefName|targetDefName|targetQuery|targetPawnName|recipeDefName|sourceDialogue|delayUnit|executionMode|stopConditionType|stopConditionDefName|stopComparison|stopUnit)\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase);
    private static readonly Regex IntField = new Regex("\\\"(?<key>delayAmount|stopAmount|jobCountPerActor)\\\"\\s*:\\s*(?<value>\\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex NumberField = new Regex("\\\"(?<key>stopThreshold)\\\"\\s*:\\s*(?<value>(?:\\d+(?:\\.\\d+)?|\\.\\d+))", RegexOptions.IgnoreCase);

    public static bool TrySchedule(Pawn pawn, string json, string fallbackDialogue = "")
    {
        if (pawn == null || string.IsNullOrWhiteSpace(json)) { Log.Warning("[RimInfluence] schedule rejected: empty pawn or JSON"); return false; }
        if (json.IndexOf("resolve_rimworld_intent", StringComparison.OrdinalIgnoreCase) < 0) { Log.Message("[RimInfluence] response contained no decision tool marker"); return false; }
        Pawn actor = ResolveActor(pawn, Field(json, "actor"));
        if (actor == null) return false;
        string capabilityId = Field(json, "capabilityId");
        string actionText = NormalizeAction(Field(json, "action"));
        if (string.IsNullOrWhiteSpace(capabilityId) && Enum.TryParse(actionText, true, out InfluenceAction oldAction))
            capabilityId = "legacy:" + oldAction;
        if (!CapabilityCatalog.IsKnown(capabilityId))
        { Log.Warning($"[RimInfluence] schedule rejected: unknown or unverified capability '{capabilityId}'"); return false; }
        InfluenceAction action = InfluenceAction.Custom;
        if (capabilityId.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase))
            Enum.TryParse(capabilityId.Substring(7), true, out action);
        int delayAmount = IntFieldValue(json, "delayAmount");
        string delayUnit = Field(json, "delayUnit");
        if (!TryResolveExecutionTick(delayAmount, delayUnit, out int executeAt)) return false;
        var component = Find.World.GetComponent<RimInfluenceWorldComponent>();
        if (component == null) { Log.Warning("[RimInfluence] schedule rejected: WorldComponent unavailable"); return false; }
        string sourceDialogue = Field(json, "sourceDialogue");
        if (string.IsNullOrWhiteSpace(sourceDialogue)) sourceDialogue = fallbackDialogue;
        var task = component.AddPlan(actor, action, Field(json, "targetDefName"), executeAt, sourceDialogue);
        task.CapabilityId = capabilityId;
        task.JobDefName = "";
        task.TargetQuery = Field(json, "targetQuery");
        if (string.Equals(capabilityId, "interaction:Arrest", StringComparison.OrdinalIgnoreCase)
            || string.Equals(capabilityId, "interaction:Attack", StringComparison.OrdinalIgnoreCase))
        {
            string targetPawnName = Field(json, "targetPawnName");
            if (!string.IsNullOrWhiteSpace(targetPawnName)) task.TargetQuery = targetPawnName;
        }
        task.RecipeDefName = Field(json, "recipeDefName");
        if (!TryConfigureExecutionPolicy(task, json))
        {
            component.Tasks.Remove(task);
            return false;
        }
        Log.Message($"[RimInfluence] scheduled id={task.Id} pawn={actor.LabelShort} capability={capabilityId} target={task.TargetDefName} query='{task.TargetQuery}' now={GenTicks.TicksGame} delay={delayAmount} {delayUnit} tick={executeAt} mode={task.ExecutionMode} stop={task.StopConditionType}");
        return true;
    }

    internal static bool TrySchedule(Pawn pawn, IDictionary<string, object> assignment, string fallbackDialogue = "")
    {
        return assignment != null && TrySchedule(pawn, AssignmentEnvelope(assignment), fallbackDialogue);
    }

    public static bool TryCancel(Pawn pawn, string json)
    {
        if (pawn == null || string.IsNullOrWhiteSpace(json)) return false;
        Pawn actor = ResolveActor(pawn, Field(json, "actor"));
        if (actor == null) return false;
        string capabilityId = Field(json, "capabilityId");
        if (string.Equals(capabilityId, "none", StringComparison.OrdinalIgnoreCase)) capabilityId = "";
        string query = Field(json, "targetQuery");
        RimInfluenceWorldComponent component = Find.World.GetComponent<RimInfluenceWorldComponent>();
        int cancelled = component?.CancelTasks(actor, capabilityId, query) ?? 0;
        Log.Message($"[RimInfluence] cancellation request actor={actor.LabelShort} capability={capabilityId} query='{query}' cancelled={cancelled}");
        return cancelled > 0;
    }

    internal static bool TryCancel(Pawn pawn, IDictionary<string, object> assignment)
    {
        return assignment != null && TryCancel(pawn, AssignmentEnvelope(assignment));
    }

    private static string AssignmentEnvelope(IDictionary<string, object> assignment)
    {
        var json = new StringBuilder("{\"tool\":\"resolve_rimworld_intent\",\"arguments\":{");
        bool first = true;
        foreach (KeyValuePair<string, object> pair in assignment)
        {
            if (!first) json.Append(',');
            first = false;
            json.Append('"').Append(JsonEscape(pair.Key)).Append("\":");
            if (pair.Value == null) json.Append("null");
            else if (pair.Value is bool boolean) json.Append(boolean ? "true" : "false");
            else if (pair.Value is byte || pair.Value is sbyte || pair.Value is short || pair.Value is ushort
                     || pair.Value is int || pair.Value is uint || pair.Value is long || pair.Value is ulong
                     || pair.Value is float || pair.Value is double || pair.Value is decimal)
                json.Append(Convert.ToString(pair.Value, CultureInfo.InvariantCulture));
            else json.Append('"').Append(JsonEscape(pair.Value.ToString())).Append('"');
        }
        return json.Append("}}").ToString();
    }

    private static string JsonEscape(string value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static Pawn ResolveActor(Pawn requestPawn, string actorName)
    {
        if (string.IsNullOrWhiteSpace(actorName))
        {
            Log.Warning($"[RimInfluence] schedule actor omitted; using request initiator {requestPawn.LabelShort}");
            return requestPawn;
        }
        if (string.Equals(requestPawn.LabelShort, actorName.Trim(), StringComparison.OrdinalIgnoreCase)) return requestPawn;
        if (requestPawn.Map == null)
        {
            Log.Warning($"[RimInfluence] schedule rejected: actor '{actorName}' cannot be resolved without a map");
            return null;
        }
        Pawn[] matches = requestPawn.Map.mapPawns.AllPawnsSpawned
            .Where(p => p != null && string.Equals(p.LabelShort, actorName.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (matches.Length == 1) return matches[0];
        Log.Warning($"[RimInfluence] schedule rejected: actor '{actorName}' matched {matches.Length} spawned pawns");
        return null;
    }

    private static bool TryResolveExecutionTick(int amount, string unit, out int executeAt)
    {
        executeAt = -1;
        if (amount < 0)
        {
            Log.Warning("[RimInfluence] schedule rejected: delayAmount is missing or negative");
            return false;
        }

        long ticksPerUnit;
        if (unit.Equals("Minute", StringComparison.OrdinalIgnoreCase)) ticksPerUnit = GenDate.TicksPerHour / 60;
        else if (unit.Equals("Hour", StringComparison.OrdinalIgnoreCase)) ticksPerUnit = GenDate.TicksPerHour;
        else if (unit.Equals("Day", StringComparison.OrdinalIgnoreCase)) ticksPerUnit = GenDate.TicksPerDay;
        else
        {
            Log.Warning($"[RimInfluence] schedule rejected: unsupported delayUnit '{unit}'");
            return false;
        }

        long delayTicks = (long)amount * ticksPerUnit;
        long targetTick = (long)GenTicks.TicksGame + Math.Max(1L, delayTicks);
        if (targetTick > int.MaxValue)
        {
            Log.Warning($"[RimInfluence] schedule rejected: delay {amount} {unit} exceeds the game tick range");
            return false;
        }
        executeAt = (int)targetTick;
        return true;
    }

    private static bool TryConfigureExecutionPolicy(ScheduledTask task, string json)
    {
        string modeText = Field(json, "executionMode");
        if (string.IsNullOrWhiteSpace(modeText)) modeText = "Once";
        if (!Enum.TryParse(modeText, true, out TaskExecutionMode mode))
        {
            Log.Warning($"[RimInfluence] schedule rejected: unsupported executionMode '{modeText}'");
            return false;
        }
        task.ExecutionMode = mode;
        string stopTypeText = Field(json, "stopConditionType");
        int requestedAmount = IntFieldValue(json, "stopAmount");
        int countPerActor = IntFieldValue(json, "jobCountPerActor");
        if (countPerActor > 1)
        {
            task.ExecutionMode = TaskExecutionMode.RepeatUntil;
            stopTypeText = nameof(TaskStopConditionType.IterationCount);
            requestedAmount = countPerActor;
            Log.Message($"[RimInfluence] using structured per-actor count id={task.Id} count={countPerActor}");
        }
        else if (mode == TaskExecutionMode.Once
            && stopTypeText.Equals(nameof(TaskStopConditionType.IterationCount), StringComparison.OrdinalIgnoreCase)
            && requestedAmount > 1)
        {
            task.ExecutionMode = TaskExecutionMode.RepeatUntil;
            Log.Message($"[RimInfluence] normalized counted task id={task.Id} count={requestedAmount}");
        }
        if (task.ExecutionMode == TaskExecutionMode.Once) return true;

        if (!Enum.TryParse(stopTypeText, true, out TaskStopConditionType stopType))
        {
            Log.Warning("[RimInfluence] repeated schedule rejected: missing verifiable stop condition");
            return false;
        }
        if (stopType == TaskStopConditionType.None)
        {
            stopType = TaskStopConditionType.NoMoreTargets;
            Log.Warning("[RimInfluence] repeated schedule omitted its stop condition; defaulting to NoMoreTargets");
        }
        task.StopConditionType = stopType;
        task.StopConditionDefName = Field(json, "stopConditionDefName");
        task.StopComparison = Enum.TryParse(Field(json, "stopComparison"), true, out TaskStopComparison comparison)
            ? comparison : TaskStopComparison.AtOrBelow;
        task.StopThreshold = NumberFieldValue(json, "stopThreshold");
        task.StopAmount = requestedAmount;

        if ((stopType == TaskStopConditionType.NeedLevel && (string.IsNullOrWhiteSpace(task.StopConditionDefName)
                || task.StopThreshold < 0f || task.StopThreshold > 1f))
            || (stopType == TaskStopConditionType.HealthPercent && (task.StopThreshold < 0f || task.StopThreshold > 1f))
            || (stopType == TaskStopConditionType.IterationCount && task.StopAmount <= 0))
        {
            Log.Warning($"[RimInfluence] repeated schedule rejected: invalid {stopType} parameters");
            return false;
        }
        if (stopType == TaskStopConditionType.ElapsedTime)
        {
            if (!TryDurationTicks(task.StopAmount, Field(json, "stopUnit"), out int durationTicks)) return false;
            long stopAt = (long)task.ExecuteAtTick + durationTicks;
            if (stopAt > int.MaxValue) return false;
            task.StopAtTick = (int)stopAt;
        }
        return true;
    }

    private static bool TryDurationTicks(int amount, string unit, out int durationTicks)
    {
        durationTicks = -1;
        if (amount <= 0) return false;
        long ticksPerUnit;
        if (unit.Equals("Minute", StringComparison.OrdinalIgnoreCase)) ticksPerUnit = GenDate.TicksPerHour / 60;
        else if (unit.Equals("Hour", StringComparison.OrdinalIgnoreCase)) ticksPerUnit = GenDate.TicksPerHour;
        else if (unit.Equals("Day", StringComparison.OrdinalIgnoreCase)) ticksPerUnit = GenDate.TicksPerDay;
        else return false;
        long result = amount * ticksPerUnit;
        if (result <= 0 || result > int.MaxValue) return false;
        durationTicks = (int)result;
        return true;
    }

    private static string Field(string json, string key)
    {
        foreach (Match match in StringField.Matches(json))
            if (string.Equals(match.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase)) return Regex.Unescape(match.Groups["value"].Value);
        return "";
    }

    private static int IntFieldValue(string json, string key)
    {
        foreach (Match match in IntField.Matches(json))
            if (string.Equals(match.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase) && int.TryParse(match.Groups["value"].Value, out int value)) return value;
        return -1;
    }

    private static float NumberFieldValue(string json, string key)
    {
        foreach (Match match in NumberField.Matches(json))
            if (string.Equals(match.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase)
                && float.TryParse(match.Groups["value"].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value)) return value;
        return -1f;
    }

    private static string NormalizeAction(string action)
    {
        if (string.Equals(action, "pickup", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "pick_up", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "pick up", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "equip_weapon", StringComparison.OrdinalIgnoreCase)) return "Equip";
        if (string.Equals(action, "chop_wood", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "chop", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "cut_tree", StringComparison.OrdinalIgnoreCase)) return "ChopWood";
        return action;
    }
}
