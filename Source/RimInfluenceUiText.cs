using System;
using RimWorld;
using Verse;

namespace RimInfluence;

internal static class RimInfluenceUiText
{
    public const string Version = "0.1.2-beta";
    private static string Language => LanguageDatabase.activeLanguage?.folderName ?? "";
    public static string T(string chinese, string english, string japanese)
    {
        if (Language.StartsWith("Chinese", StringComparison.OrdinalIgnoreCase)) return chinese;
        if (Language.StartsWith("Japanese", StringComparison.OrdinalIgnoreCase)) return japanese;
        return english;
    }

    public static string CapabilityDescription(string id)
    {
        if (id == "need:Eat") return T("寻找食物并进食", "Find food and eat", "食事を探して食べる");
        if (id == "interaction:Ingest") return T("摄取指定的饮品、药物或食物", "Consume a specified drink, drug, or food", "指定した飲み物・薬物・食べ物を摂取する");
        if (id == "interaction:Arrest") return T("拘捕指定角色", "Arrest a named pawn", "指定した人物を逮捕する");
        if (id == "interaction:Attack") return T("攻击指定角色", "Attack a named pawn", "指定した人物を攻撃する");
        if (id == "command:StandStill") return T("留在原地等待", "Wait at the current position", "その場で待機する");
        if (id.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse(id.Substring(7), true, out InfluenceAction action))
        {
            return action switch
            {
                InfluenceAction.ChopWood => T("砍伐树木并获取木材", "Cut trees for wood", "木材を得るために木を伐る"),
                InfluenceAction.Harvest => T("收获成熟作物", "Harvest mature plants", "成熟した作物を収穫する"),
                InfluenceAction.Haul => T("搬运物品到合适的储存位置", "Haul items to storage", "物品を保管場所に運ぶ"),
                InfluenceAction.Clean => T("清理地面污物", "Clean filth", "汚れを掃除する"),
                InfluenceAction.Construct => T("建造蓝图和框架", "Build blueprints and frames", "設計図や建築途中の施設を建てる"),
                InfluenceAction.Repair => T("修理受损建筑", "Repair damaged buildings", "損傷した建築物を修理する"),
                InfluenceAction.Hunt => T("狩猎动物", "Hunt animals", "動物を狩る"),
                InfluenceAction.Mine => T("开采矿物和岩石", "Mine rock and ore", "岩石や鉱石を採掘する"),
                InfluenceAction.Sow => T("在种植区播种", "Sow growing zones", "栽培ゾーンに種をまく"),
                InfluenceAction.Butcher => T("屠宰动物尸体", "Butcher animal corpses", "動物の死体を解体する"),
                InfluenceAction.Cook => T("烹饪食物", "Cook food", "食事を調理する"),
                InfluenceAction.Craft => T("制作物品", "Craft items", "アイテムを製作する"),
                InfluenceAction.Research => T("开展研究", "Work on research", "研究を進める"),
                InfluenceAction.Doctor => T("治疗伤病角色", "Treat injured or sick pawns", "負傷者や病人を治療する"),
                InfluenceAction.Rescue => T("救援倒地角色", "Rescue downed pawns", "倒れた人物を救助する"),
                InfluenceAction.Extinguish => T("扑灭火灾", "Extinguish fires", "火を消す"),
                InfluenceAction.Tame => T("驯服野生动物", "Tame wild animals", "野生動物を手懐ける"),
                InfluenceAction.Train => T("训练已驯服动物", "Train tame animals", "飼いならした動物を訓練する"),
                InfluenceAction.Feed => T("喂食需要照顾的角色", "Feed pawns needing care", "介護が必要な人物に食事を与える"),
                InfluenceAction.Equip => T("拾取并装备武器", "Pick up and equip a weapon", "武器を拾って装備する"),
                _ => T("执行工作", "Perform work", "作業を行う")
            };
        }
        if (id.StartsWith("work:", StringComparison.OrdinalIgnoreCase))
        {
            WorkGiverDef def = DefDatabase<WorkGiverDef>.GetNamedSilentFail(id.Substring(5));
            if (def != null)
            {
                string name = string.IsNullOrWhiteSpace(def.label)
                    ? (string.IsNullOrWhiteSpace(def.gerund) ? def.verb : def.gerund) : def.label;
                if (string.IsNullOrWhiteSpace(name)) name = def.workType?.label ?? id;
                return def.emergency ? name + T("（紧急）", " (emergency)", "（緊急）") : name;
            }
        }
        return id;
    }
}
