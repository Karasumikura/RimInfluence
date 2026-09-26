using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimInfluence;

internal static class CapabilityCatalog
{
    private static readonly InfluenceAction[] LegacyActions =
    {
        InfluenceAction.ChopWood, InfluenceAction.Harvest, InfluenceAction.Haul, InfluenceAction.Clean,
        InfluenceAction.Construct, InfluenceAction.Repair, InfluenceAction.Hunt, InfluenceAction.Mine,
        InfluenceAction.Sow, InfluenceAction.Butcher, InfluenceAction.Cook, InfluenceAction.Craft,
        InfluenceAction.Research, InfluenceAction.Doctor, InfluenceAction.Rescue, InfluenceAction.Extinguish,
        InfluenceAction.Tame, InfluenceAction.Train, InfluenceAction.Feed, InfluenceAction.Equip
    };

    public static List<string> Ids()
    {
        var ids = new List<string> { "none", "need:Eat", "interaction:Ingest", "interaction:Arrest", "interaction:Attack", "command:StandStill" };
        ids.AddRange(LegacyActions.Select(a => "legacy:" + a));
        ids.AddRange(DefDatabase<WorkGiverDef>.AllDefsListForReading
            .Where(d => d?.giverClass != null)
            .OrderBy(d => d.defName)
            .Select(d => "work:" + d.defName));
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsKnown(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
        if (!RimInfluenceMod.Settings.IsCapabilityEnabled(id)) return false;
        if (!CapabilityAudit.IsStructurallyAvailable(id)) return false;
        if (id.Equals("need:Eat", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Equals("interaction:Ingest", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Equals("interaction:Arrest", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Equals("interaction:Attack", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Equals("command:StandStill", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase))
            return Enum.TryParse(id.Substring(7), true, out InfluenceAction action) && LegacyActions.Contains(action);
        if (id.StartsWith("work:", StringComparison.OrdinalIgnoreCase))
            return DefDatabase<WorkGiverDef>.GetNamedSilentFail(id.Substring(5))?.giverClass != null;
        return false;
    }

    public static string ShortCardText(string id)
    {
        // These are capability contracts, never matching rules for player utterances.
        if (id == "command:StandStill") return id + " | current position: wait without walking; indefinite unless duration specified";
        if (id == "interaction:Arrest") return id + " | named pawn: arrest and imprison; targetPawnName";
        if (id == "interaction:Attack") return id + " | named pawn: ranged/melee attack; targetPawnName";
        if (id == "need:Eat") return id + " | hunger: autonomously find a meal; no specified item";
        if (id == "interaction:Ingest") return id + " | specified ingestible item: drink, eat or use; targetDefName is the ThingDef ID";
        if (id.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse(id.Substring(7), true, out InfluenceAction action))
        {
            string effect = action switch
            {
                InfluenceAction.ChopWood => "trees: fell/remove trees for wood, not collect ripe produce",
                InfluenceAction.Harvest => "mature plants: collect harvestable produce, not fell trees for wood",
                InfluenceAction.Haul => "items: carry to suitable storage",
                InfluenceAction.Clean => "filth: clean",
                InfluenceAction.Construct => "blueprints/frames: build",
                InfluenceAction.Repair => "damaged buildings: repair",
                InfluenceAction.Hunt => "animals: hunt for meat",
                InfluenceAction.Mine => "mineable rock: excavate",
                InfluenceAction.Sow => "growing cells: sow plants",
                InfluenceAction.Butcher => "corpses: butcher using bills",
                InfluenceAction.Cook => "ingredients: cook using bills",
                InfluenceAction.Craft => "materials: craft using bills; optional recipeDefName",
                InfluenceAction.Research => "research bench: research current project",
                InfluenceAction.Doctor => "patients: medical work",
                InfluenceAction.Rescue => "downed pawns: carry to bed for rescue",
                InfluenceAction.Extinguish => "fires: extinguish",
                InfluenceAction.Tame => "wild animals: tame",
                InfluenceAction.Train => "tame animals: train",
                InfluenceAction.Feed => "patients: feed",
                InfluenceAction.Equip => "weapons: equip, not haul to storage",
                _ => RimInfluenceText.ActionName(action)
            };
            return id + " | " + effect;
        }
        CapabilityCard saved = RimInfluenceMod.Settings.Cards
            .FirstOrDefault(card => string.Equals(card?.Id, id, StringComparison.OrdinalIgnoreCase));
        if (saved != null)
        {
            WorkGiverDef describedWork = id.StartsWith("work:", StringComparison.OrdinalIgnoreCase)
                ? DefDatabase<WorkGiverDef>.GetNamedSilentFail(id.Substring(5)) : null;
            return id + " | " + (describedWork == null ? "" : WorkDescription(describedWork) + " | ")
                + Clean(saved.Description) + " | target: " + Clean(saved.Target);
        }
        return CardText(id);
    }

    public static string CardText(string id)
    {
        CapabilityCard saved = RimInfluenceMod.Settings.Cards
            .FirstOrDefault(card => string.Equals(card?.Id, id, StringComparison.OrdinalIgnoreCase));
        if (saved != null)
            return id + " | " + saved.Description + " | target: " + saved.Target
                + " | requirements: " + saved.Requirements + " | expected job: " + saved.ExpectedJob;
        if (id.Equals("interaction:Arrest", StringComparison.OrdinalIgnoreCase))
            return "interaction:Arrest | arrest / 拘捕 a named pawn; attempts an Arrest job. Requires a reachable target and prisoner bed. Not prisoner transport.";
        if (id.Equals("interaction:Attack", StringComparison.OrdinalIgnoreCase))
            return "interaction:Attack | attack / 攻击 a specifically named pawn using RimWorld's native ranged or melee order. Requires a valid target and a pawn capable of violence.";
        if (id.Equals("command:StandStill", StringComparison.OrdinalIgnoreCase))
            return "command:StandStill | stand still / 原地等待 at the pawn's current position, without wandering. Uses the native Wait job. For a specified duration use RepeatUntil with ElapsedTime and stopAmount/stopUnit; without a duration continue until explicitly cancelled or interrupted by the game.";
        if (id.Equals("need:Eat", StringComparison.OrdinalIgnoreCase))
            return "need:Eat | satisfy hunger using native food selection, without selecting a particular item";
        if (id.Equals("interaction:Ingest", StringComparison.OrdinalIgnoreCase))
            return "interaction:Ingest | consume / 摄取 a specified drink, drug or food item. Supply its ThingDef in targetDefName, or its displayed name in targetQuery. Uses the native Ingest job on a reachable, reservable, ingestible item. Unlike need:Eat, this does not search for a meal.";
        if (id.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse(id.Substring(7), true, out InfluenceAction action))
            return ShortCardText(id);
        if (id.StartsWith("work:", StringComparison.OrdinalIgnoreCase))
        {
            WorkGiverDef def = DefDatabase<WorkGiverDef>.GetNamedSilentFail(id.Substring(5));
            if (def != null) return id + " | " + WorkDescription(def);
        }
        return id;
    }

    public static string WorkDescription(WorkGiverDef def)
    {
        if (def == null) return "";
        string label = Clean(def.label);
        if (string.IsNullOrEmpty(label)) label = Clean(def.gerund.NullOrEmpty() ? def.verb : def.gerund);
        if (def.emergency) label += " | emergency";
        return label + (def.workType == null ? "" : " | " + Clean(def.workType.label));
    }

    public static string StopConditionCatalog()
    {
        var needs = DefDatabase<NeedDef>.AllDefsListForReading
            .OrderBy(d => d.defName)
            .Select(d => d.defName + " | " + Clean(d.label));
        return "Available generic stop conditions: NoMoreTargets, NeedLevel, HealthPercent, ElapsedTime, IterationCount. "
            + "Use NoMoreTargets for all/every/全部/砍光/搬完/清完 requests.\n"
            + "Loaded NeedDefs for NeedLevel:\n" + string.Join("\n", needs);
    }

    public static string Label(string id, InfluenceAction fallback)
    {
        if (id.Equals("need:Eat", StringComparison.OrdinalIgnoreCase))
            return (LanguageDatabase.activeLanguage?.folderName ?? "").StartsWith("Chinese", StringComparison.OrdinalIgnoreCase) ? "吃东西" : "eating";
        if (id.Equals("interaction:Ingest", StringComparison.OrdinalIgnoreCase))
            return RimInfluenceUiText.T("摄取物品", "ingesting", "摂取");
        if (id.Equals("interaction:Arrest", StringComparison.OrdinalIgnoreCase))
            return (LanguageDatabase.activeLanguage?.folderName ?? "").StartsWith("Chinese", StringComparison.OrdinalIgnoreCase) ? "拘捕" : "arresting";
        if (id.Equals("interaction:Attack", StringComparison.OrdinalIgnoreCase))
            return (LanguageDatabase.activeLanguage?.folderName ?? "").StartsWith("Chinese", StringComparison.OrdinalIgnoreCase) ? "攻击" : "attacking";
        if (id.Equals("command:StandStill", StringComparison.OrdinalIgnoreCase))
            return (LanguageDatabase.activeLanguage?.folderName ?? "").StartsWith("Chinese", StringComparison.OrdinalIgnoreCase) ? "原地等待" : "standing still";
        if (id.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse(id.Substring(7), true, out InfluenceAction action)) return RimInfluenceText.ActionName(action);
        if (id.StartsWith("work:", StringComparison.OrdinalIgnoreCase))
        {
            WorkGiverDef def = DefDatabase<WorkGiverDef>.GetNamedSilentFail(id.Substring(5));
            if (def != null) return Clean(def.gerund.NullOrEmpty() ? def.verb : def.gerund);
        }
        return RimInfluenceText.ActionName(fallback);
    }

    private static string Clean(string value) => (value ?? "").Replace("\n", " ").Replace("\r", " ").Trim();
}
