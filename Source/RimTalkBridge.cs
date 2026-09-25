using System;
using System.Linq;
using System.Reflection;
using Verse;

namespace RimInfluence;

internal static class RimTalkBridge
{
    public static bool TryReportFailure(Pawn pawn, string original, string failure)
    {
        return Send(pawn, $"任务失败反馈：{failure}\n原计划：{original}\n这是游戏执行器返回的实际结果。请按原有角色设定和对话格式告诉玩家任务没有完成及原因，不要虚构已经成功，也不要安排新任务。", "Event");
    }

    public static bool TryWake(Pawn pawn, string original, string failure)
    {
        return Send(pawn, $"任务失败：{failure}\n原计划：{original}\n请说明具体原因，并提出下一步可执行安排。", "Event");
    }

    private static bool Send(Pawn pawn, string prompt, string talkTypeName)
    {
        if (pawn == null) return false;
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RimTalk");
            var busy = asm?.GetType("RimTalk.Service.AIService")?.GetMethod("IsBusy", BindingFlags.Public | BindingFlags.Static);
            if (busy == null || (bool)busy.Invoke(null, null)) return false;
            var requestType = asm?.GetType("RimTalk.Data.TalkRequest");
            var serviceType = asm?.GetType("RimTalk.Service.TalkService");
            var talkType = asm?.GetType("RimTalk.Source.Data.TalkType");
            var generate = serviceType?.GetMethod("GenerateTalk", BindingFlags.Public | BindingFlags.Static);
            if (requestType == null || generate == null) return false;
            object request = Activator.CreateInstance(requestType, prompt, pawn, null, Enum.Parse(talkType, talkTypeName));
            bool accepted = (bool)generate.Invoke(null, new[] { request });
            if (accepted) Log.Message($"[RimInfluence] failure dialogue submitted pawn={pawn.LabelShort}; task will not be retried");
            return accepted;
        }
        catch (Exception ex) { Log.Warning($"[RimInfluence] RimTalk failure report failed: {ex.Message}"); return false; }
    }
}
