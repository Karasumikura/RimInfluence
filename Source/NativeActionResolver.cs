using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimInfluence;

internal static class NativeActionResolver
{
    private static readonly Type ProviderType = typeof(FloatMenuMakerMap).Assembly.GetType("RimWorld.FloatMenuOptionProvider_WorkGivers");
    private static readonly MethodInfo GetOptions = ProviderType?.GetMethod("GetWorkGiversOptionsFor", BindingFlags.Instance | BindingFlags.NonPublic);

    public static bool TryExecute(ScheduledTask task)
    {
        if (task?.Pawn?.Map == null || ProviderType == null || GetOptions == null) return false;
        HashSet<string> acceptedJobs = AcceptedJobs(task.Action);
        if (acceptedJobs == null) return false;

        IEnumerable<Thing> candidates = CandidateThings(task)
            .Where(t => t != null && t.Spawned && !t.IsForbidden(task.Pawn))
            .Where(t => task.Pawn.CanReach(t, PathEndMode.Touch, Danger.Some))
            .Where(t => task.Pawn.CanReserve(t))
            .Where(t => MatchesQuery(t, task.TargetQuery))
            .OrderBy(t => task.Pawn.Position.DistanceToSquared(t.Position))
            .Take(128);

        foreach (Thing target in candidates)
        {
            FloatMenuOption option = FindOption(task.Pawn, target, acceptedJobs, out Job nativeJob);
            if (option == null || nativeJob == null || option.Disabled) continue;
            if (RimInfluenceMod.Settings.MarkTriggeredJobs)
                nativeJob.reportStringOverride = RimInfluenceText.JobReport(task.Action, target);
            option.action();
            bool accepted = task.Pawn.jobs?.curJob == nativeJob;
            if (accepted) task.ActiveJob = nativeJob;
            Log.Message($"[RimInfluence] native action: action={task.Action}, option='{option.Label}', job={nativeJob.def.defName}, target={target.LabelShort}, accepted={accepted}");
            if (accepted) return true;
        }
        Log.Message($"[RimInfluence] no native action found: action={task.Action}, query='{task.TargetQuery}'");
        return false;
    }

    private static FloatMenuOption FindOption(Pawn pawn, Thing target, HashSet<string> acceptedJobs, out Job selectedJob)
    {
        selectedJob = null;
        try
        {
            object provider = Activator.CreateInstance(ProviderType);
            var pawns = new List<Pawn> { pawn };
            Vector3 click = target.Position.ToVector3Shifted();
            var context = new FloatMenuContext(pawns, click, pawn.Map);
            object result = GetOptions.Invoke(provider, new object[] { pawn, (LocalTargetInfo)target, context });
            if (!(result is IEnumerable options)) return null;
            foreach (object value in options)
            {
                if (!(value is FloatMenuOption option) || option.Disabled) continue;
                Job job = FindCapturedJob(option.action?.Target);
                if (job?.def != null && acceptedJobs.Contains(job.def.defName))
                {
                    selectedJob = job;
                    return option;
                }
            }
        }
        catch (Exception ex) { Log.Warning($"[RimInfluence] native action query failed for {target.LabelShort}: {ex.GetBaseException().Message}"); }
        return null;
    }

    private static Job FindCapturedJob(object closure)
    {
        if (closure == null) return null;
        foreach (FieldInfo field in closure.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object value;
            try { value = field.GetValue(closure); } catch { continue; }
            if (value is Job job) return job;
        }
        return null;
    }

    private static IEnumerable<Thing> CandidateThings(ScheduledTask task)
    {
        IEnumerable<Thing> all = task.Pawn.Map.listerThings.AllThings;
        switch (task.Action)
        {
            case InfluenceAction.ChopWood:
                return all.OfType<Plant>().Where(p => p.def?.plant?.IsTree == true && !p.def.plant.isStump);
            case InfluenceAction.Harvest:
                return all.OfType<Plant>().Where(p => p.HarvestableNow);
            case InfluenceAction.Clean:
                return all.OfType<Filth>();
            case InfluenceAction.Mine:
                return all.Where(t => t is Mineable);
            case InfluenceAction.Construct:
                return all.Where(t => t is Frame || t is Blueprint);
            case InfluenceAction.Repair:
                return all.Where(t => t.def?.building != null && t.HitPoints < t.MaxHitPoints);
            case InfluenceAction.Hunt:
                return all.OfType<Pawn>().Where(p => p.RaceProps?.Animal == true && !p.Dead);
            case InfluenceAction.Tame:
            case InfluenceAction.Train:
                return all.OfType<Pawn>().Where(p => p.RaceProps?.Animal == true && !p.Dead);
            case InfluenceAction.Doctor:
            case InfluenceAction.Rescue:
            case InfluenceAction.Feed:
                return all.OfType<Pawn>().Where(p => p != task.Pawn && !p.Dead && (p.Downed || p.health?.HasHediffsNeedingTend() == true));
            case InfluenceAction.Research:
                return all.Where(t => t is Building_ResearchBench);
            default:
                return Enumerable.Empty<Thing>();
        }
    }

    private static HashSet<string> AcceptedJobs(InfluenceAction action)
    {
        string[] names = action switch
        {
            InfluenceAction.ChopWood => new[] { "CutPlant", "CutPlantDesignated" },
            InfluenceAction.Harvest => new[] { "Harvest", "HarvestDesignated" },
            InfluenceAction.Clean => new[] { "Clean" },
            InfluenceAction.Mine => new[] { "Mine" },
            InfluenceAction.Construct => new[] { "ConstructFinishFrame", "PlaceNoCostFrame" },
            InfluenceAction.Repair => new[] { "Repair" },
            InfluenceAction.Hunt => new[] { "Hunt" },
            InfluenceAction.Tame => new[] { "Tame" },
            InfluenceAction.Train => new[] { "Train" },
            InfluenceAction.Doctor => new[] { "TendPatient", "TendPatientWithoutMedicine" },
            InfluenceAction.Rescue => new[] { "Rescue" },
            InfluenceAction.Feed => new[] { "FeedPatient" },
            InfluenceAction.Research => new[] { "Research" },
            _ => null
        };
        return names == null ? null : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesQuery(Thing thing, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (thing.def.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || thing.LabelCap.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return thing is Plant plant && plant.def?.plant?.IsTree == true
            && (query.Equals("tree", StringComparison.OrdinalIgnoreCase) || query.IndexOf("树", StringComparison.OrdinalIgnoreCase) >= 0);
    }

}
