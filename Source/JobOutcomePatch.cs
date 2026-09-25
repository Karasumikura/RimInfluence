using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimInfluence;

[StaticConstructorOnStartup]
internal static class JobOutcomePatch
{
    private static readonly FieldInfo PawnField = AccessTools.Field(typeof(Pawn_JobTracker), "pawn");

    static JobOutcomePatch()
    {
        try
        {
            MethodInfo endCurrentJob = AccessTools.Method(typeof(Pawn_JobTracker), "EndCurrentJob",
                new[] { typeof(JobCondition), typeof(bool), typeof(bool) });
            if (endCurrentJob == null) throw new MissingMethodException("Pawn_JobTracker.EndCurrentJob");
            new Harmony("karasumikura.riminfluence.joboutcome").Patch(endCurrentJob,
                prefix: new HarmonyMethod(typeof(JobOutcomePatch), nameof(EndCurrentJobPrefix)));
            Log.Message("[RimInfluence] native JobCondition outcome tracking installed");
        }
        catch (Exception ex) { Log.Warning("[RimInfluence] job outcome tracking unavailable: " + ex); }
    }

    private static void EndCurrentJobPrefix(Pawn_JobTracker __instance, JobCondition condition)
    {
        try
        {
            Job job = __instance?.curJob;
            Pawn pawn = PawnField?.GetValue(__instance) as Pawn;
            if (job == null || pawn == null || Find.World == null) return;
            CapabilityAutoTest.JobEnded(pawn, job, condition);
            Find.World.GetComponent<RimInfluenceWorldComponent>()?.NotifyJobEnded(pawn, job, condition);
        }
        catch (Exception ex) { Log.Warning("[RimInfluence] could not record job outcome: " + ex.Message); }
    }
}
