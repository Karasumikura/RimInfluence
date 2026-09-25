using System;
using Verse;

namespace RimInfluence;

internal static class RimInfluenceText
{
    private static bool IsChinese
    {
        get
        {
            string folder = LanguageDatabase.activeLanguage?.folderName ?? "";
            return folder.StartsWith("Chinese", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string JobReport(InfluenceAction action, Thing target = null)
    {
        string targetName = target == null ? "" : " " + target.LabelShort;
        return IsChinese
            ? "RimInfluence：正在" + ActionName(action) + targetName
            : "RimInfluence: " + ActionProgress(action) + targetName;
    }

    public static string JobReport(JobDef jobDef, Thing target = null)
    {
        if (jobDef == null) return JobReport(InfluenceAction.Work, target);
        string targetName = target == null ? "" : " " + target.LabelShort;
        return IsChinese
            ? "RimInfluence：正在执行" + (jobDef.label ?? jobDef.defName) + targetName
            : "RimInfluence: " + (jobDef.label ?? jobDef.defName) + targetName;
    }

    public static string Completed(Pawn pawn, InfluenceAction action)
    {
        string pawnName = pawn?.LabelShort ?? (IsChinese ? "角色" : "Pawn");
        return IsChinese
            ? "RimInfluence：" + pawnName + "已完成" + ActionName(action) + "。"
            : "RimInfluence: " + pawnName + " completed " + ActionName(action) + ".";
    }

    public static string Completed(ScheduledTask task)
    {
        string pawnName = task?.Pawn?.LabelShort ?? (IsChinese ? "角色" : "Pawn");
        string label = CapabilityCatalog.Label(task?.CapabilityId ?? "", task?.Action ?? InfluenceAction.Work);
        return IsChinese ? "RimInfluence：" + pawnName + "已完成" + label + "。"
            : "RimInfluence: " + pawnName + " completed " + label + ".";
    }

    public static string Failed(Pawn pawn, InfluenceAction action, string reason)
    {
        string pawnName = pawn?.LabelShort ?? (IsChinese ? "角色" : "Pawn");
        return IsChinese
            ? "RimInfluence：" + pawnName + "未能完成" + ActionName(action) + "：" + reason
            : "RimInfluence: " + pawnName + " could not complete " + ActionName(action) + ": " + reason;
    }

    public static string Failed(ScheduledTask task, string reason)
    {
        string pawnName = task?.Pawn?.LabelShort ?? (IsChinese ? "角色" : "Pawn");
        string label = CapabilityCatalog.Label(task?.CapabilityId ?? "", task?.Action ?? InfluenceAction.Work);
        return IsChinese ? "RimInfluence：" + pawnName + "未能完成" + label + "：" + reason
            : "RimInfluence: " + pawnName + " could not complete " + label + ": " + reason;
    }

    public static string PawnUnavailable(bool whileWorking)
    {
        if (IsChinese) return whileWorking ? "角色在执行任务时已不可用。" : "角色不可用或不在地图上。";
        return whileWorking ? "Pawn became unavailable while working." : "Pawn is unavailable or not on a map.";
    }

    public static string NoExecutableTarget(InfluenceAction action, string targetDefName)
    {
        if (IsChinese) return "未找到可执行的" + ActionName(action) + "目标" + (string.IsNullOrEmpty(targetDefName) ? "。" : "（" + targetDefName + "）。");
        return "No executable target was found for " + ActionName(action) + (string.IsNullOrEmpty(targetDefName) ? "." : " (" + targetDefName + ").");
    }

    public static string ActionName(InfluenceAction action)
    {
        if (IsChinese)
        {
            return action switch
            {
                InfluenceAction.Harvest => "收获",
                InfluenceAction.Haul => "搬运",
                InfluenceAction.Clean => "清扫",
                InfluenceAction.Construct => "建造",
                InfluenceAction.Repair => "维修",
                InfluenceAction.Hunt => "狩猎",
                InfluenceAction.Mine => "采矿",
                InfluenceAction.Plant => "种植",
                InfluenceAction.ChopWood => "砍树",
                InfluenceAction.Sow => "播种",
                InfluenceAction.Butcher => "屠宰",
                InfluenceAction.Cook => "烹饪",
                InfluenceAction.Craft => "制作",
                InfluenceAction.Research => "研究",
                InfluenceAction.Doctor => "治疗",
                InfluenceAction.Rescue => "救援",
                InfluenceAction.Extinguish => "灭火",
                InfluenceAction.Tame => "驯服",
                InfluenceAction.Train => "训练",
                InfluenceAction.Feed => "喂食",
                InfluenceAction.Equip => "装备",
                _ => "工作"
            };
        }

        return action switch
        {
            InfluenceAction.Harvest => "harvesting",
            InfluenceAction.Haul => "hauling",
            InfluenceAction.Clean => "cleaning",
            InfluenceAction.Construct => "constructing",
            InfluenceAction.Repair => "repairing",
            InfluenceAction.Hunt => "hunting",
            InfluenceAction.Mine => "mining",
            InfluenceAction.Plant => "planting",
            InfluenceAction.ChopWood => "cutting wood",
            InfluenceAction.Sow => "sowing",
            InfluenceAction.Butcher => "butchering",
            InfluenceAction.Cook => "cooking",
            InfluenceAction.Craft => "crafting",
            InfluenceAction.Research => "researching",
            InfluenceAction.Doctor => "tending",
            InfluenceAction.Rescue => "rescuing",
            InfluenceAction.Extinguish => "extinguishing",
            InfluenceAction.Tame => "taming",
            InfluenceAction.Train => "training",
            InfluenceAction.Feed => "feeding",
            InfluenceAction.Equip => "equipping",
            _ => "working"
        };
    }

    private static string ActionProgress(InfluenceAction action) => ActionName(action);
}
