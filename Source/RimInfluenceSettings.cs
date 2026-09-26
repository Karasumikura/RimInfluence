using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimInfluence;

public sealed class RimInfluenceSettings : ModSettings
{
    public bool RetryFailedTasks = false;
    public bool ReportFailureDialogue = true;
    public bool EnableMultiTurn = true;
    public int MaxContinuationRounds = 3;
    public bool MarkTriggeredJobs = true;
    public int RetryCooldownTicks = 60000;
    public List<CapabilityCard> Cards = new List<CapabilityCard>();
    public List<string> DisabledCapabilities = new List<string>();

    public bool IsCapabilityEnabled(string id) => !DisabledCapabilities.Contains(id, StringComparer.OrdinalIgnoreCase);

    public void SetCapabilityEnabled(string id, bool enabled)
    {
        if (enabled) DisabledCapabilities.RemoveAll(value => string.Equals(value, id, StringComparison.OrdinalIgnoreCase));
        else if (IsCapabilityEnabled(id)) DisabledCapabilities.Add(id);
        Write();
    }

    public override void ExposeData()
    {
        Scribe_Values.Look(ref ReportFailureDialogue, "reportFailureDialogue", true);
        Scribe_Values.Look(ref EnableMultiTurn, "enableMultiTurn", true);
        Scribe_Values.Look(ref MaxContinuationRounds, "maxContinuationRounds", 3);
        Scribe_Values.Look(ref MarkTriggeredJobs, "markTriggeredJobs", true);
        Scribe_Values.Look(ref RetryCooldownTicks, "retryCooldownTicks", 60000);
        Scribe_Collections.Look(ref Cards, "capabilityCards", LookMode.Deep);
        Scribe_Collections.Look(ref DisabledCapabilities, "disabledCapabilities", LookMode.Value);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            Cards ??= new List<CapabilityCard>();
            DisabledCapabilities ??= new List<string>();
            MaxContinuationRounds = Math.Max(1, Math.Min(20, MaxContinuationRounds));
        }
    }
}

public sealed class RimInfluenceMod : Mod
{
    public static RimInfluenceSettings Settings => _settings ??= LoadedModManager.GetMod<RimInfluenceMod>().GetSettings<RimInfluenceSettings>();
    private static RimInfluenceSettings _settings;
    private string _maxContinuationRoundsBuffer;

    public RimInfluenceMod(ModContentPack content) : base(content) { _settings = GetSettings<RimInfluenceSettings>(); }

    public override string SettingsCategory() => "RimInfluence";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var listing = new Listing_Standard();
        listing.Begin(inRect);
        listing.Label("RimInfluence v" + RimInfluenceUiText.Version);
        listing.Gap(8f);
        listing.CheckboxLabeled(RimInfluenceUiText.T("任务失败后由 NPC 反馈", "NPC explains failed tasks", "タスク失敗時にNPCが説明する"),
            ref Settings.ReportFailureDialogue,
            RimInfluenceUiText.T("让 RimTalk 根据实际失败原因生成角色对话。", "RimTalk generates an in-character explanation of the failure.",
                "RimTalkが実際の失敗理由をキャラクターの会話として伝えます。"));
        listing.CheckboxLabeled(RimInfluenceUiText.T("执行结果后继续对话", "Continue dialogue after results", "実行結果から会話を続ける"),
            ref Settings.EnableMultiTurn);
        listing.Label(RimInfluenceUiText.T("最大续轮数（1-20）", "Maximum continuation rounds (1-20)", "継続会話の最大回数（1-20）"));
        _maxContinuationRoundsBuffer ??= Settings.MaxContinuationRounds.ToString();
        Widgets.TextFieldNumeric(listing.GetRect(30f), ref Settings.MaxContinuationRounds,
            ref _maxContinuationRoundsBuffer, 1, 20);
        listing.CheckboxLabeled(RimInfluenceUiText.T("标记 RimInfluence 派发的工作", "Mark RimInfluence jobs", "RimInfluenceの仕事を表示"),
            ref Settings.MarkTriggeredJobs,
            RimInfluenceUiText.T("在角色当前工作说明中显示 RimInfluence 标记。", "Show RimInfluence in the pawn's current job label.",
                "現在の仕事名にRimInfluenceを表示します。"));
        listing.Gap(8f);
        if (listing.ButtonText(RimInfluenceUiText.T("为新 Mod 生成能力说明", "Describe new mod capabilities", "新しいModの能力説明を生成")) && !ModCapabilityImporter.Busy)
            ModCapabilityImporter.Start();
        listing.Label(ModCapabilityImporter.Status);
        listing.Gap(8f);
        if (listing.ButtonText(RimInfluenceUiText.T("刷新能力状态", "Refresh capability status", "能力の状態を更新")))
            CapabilityAudit.Refresh(Find.World?.GetComponent<RimInfluenceWorldComponent>()?.Tasks);
        listing.Label(CapabilityAudit.Summary);
        if (listing.ButtonText(RimInfluenceUiText.T("查看能力状态", "View capability status", "能力の状態を見る")))
            Find.WindowStack.Add(new CapabilityStatusWindow());
        if (listing.ButtonText(RimInfluenceUiText.T("复制报告路径", "Copy report path", "レポートの場所をコピー")))
        {
            GUIUtility.systemCopyBuffer = CapabilityAudit.ReportPath;
            Messages.Message(RimInfluenceUiText.T("报告路径已复制。", "Report path copied.", "レポートの場所をコピーしました。"), MessageTypeDefOf.NeutralEvent);
        }
        listing.End();
        base.DoSettingsWindowContents(inRect);
    }
}
