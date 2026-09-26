using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RimInfluence;

internal static class Program
{
    private static int passed;
    private static void Main()
    {
        string rimtalk = Environment.GetEnvironmentVariable("RIMTALK_ASSEMBLIES")
            ?? @"D:\SteamLibrary\steamapps\workshop\content\294100\3551203752\1.6\Assemblies";
        string managed = Path.Combine(Environment.GetEnvironmentVariable("RIMWORLD_DIR")
            ?? @"D:\SteamLibrary\steamapps\common\RimWorld", "RimWorldWin64_Data", "Managed");
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string dir in new[] { rimtalk, managed })
                if (File.Exists(Path.Combine(dir, name))) return Assembly.LoadFrom(Path.Combine(dir, name));
            return null;
        };
        Assembly.LoadFrom(Path.Combine(rimtalk, "RimTalk.dll"));
        Run();
        TransportTests.Run();
        Console.WriteLine("PASS " + passed + " protocol checks (actual installed RimTalk JSON serializer/parser)");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        passed++;
    }

    private static void Reject(Action action, string name)
    {
        try { action(); } catch { passed++; return; }
        throw new Exception("FAIL: expected rejection: " + name);
    }

    private static void Run()
    {
        var result = RimTalkJson.Parse("{\"actionDecision\":\"Schedule\",\"dialogueResponses\":[{\"name\":\"Arthur\",\"text\":\"我停在这里。\\n等你回来。\",\"act\":\"Wait\",\"target\":\"Nelly\"}],\"assignments\":[{\"decision\":\"Schedule\",\"actors\":[\"Arthur\",\"Nelly\"],\"capabilityId\":\"command:StandStill\"}]}");
        string dialogue = UnifiedDialogueProtocol.Dialogue(result);
        Check(dialogue.Contains("Arthur") && !dialogue.Contains("assignments"), "only dialogue passed to RimTalk");
        var roundtrip = RimTalkJson.Parse("{\"items\":" + dialogue + "}");
        var spoken = RimTalkJson.Object(((IList)roundtrip["items"])[0]);
        Check(RimTalkJson.String(spoken, "text") == "我停在这里。\n等你回来。", "Chinese and escaping preserved");
        Check(RimTalkJson.String(spoken, "act") == "Wait" && RimTalkJson.String(spoken, "target") == "Nelly", "RimTalk extra fields preserved");
        Type talkResponse = HarmonyLib.AccessTools.TypeByName("RimTalk.Data.TalkResponse");
        Type streamParser = talkResponse.Assembly.GetType("RimTalk.Util.JsonStreamParser`1").MakeGenericType(talkResponse);
        object parser = Activator.CreateInstance(streamParser);
        IList parsedDialogue = (IList)streamParser.GetMethod("Parse").Invoke(parser, new object[] { dialogue });
        Check(parsedDialogue.Count == 1 && (string)talkResponse.GetProperty("Text").GetValue(parsedDialogue[0]) == "我停在这里。\n等你回来。", "installed RimTalk streaming parser accepts delivered dialogue");
        var task = RimTalkJson.Object(((IList)result["assignments"])[0]);
        var normalized = UnifiedDialogueProtocol.WithDefaults(task);
        Check((int)normalized["delayAmount"] == 0 && (string)normalized["delayUnit"] == "Hour", "immediate default");
        Check((string)normalized["executionMode"] == "Once" && !normalized.ContainsKey("stopAmount"), "indefinite wait has no invented duration");
        Check(!task.ContainsKey("delayAmount") && ((IList)normalized["actors"]).Count == 2, "copy preserves multiple actors");
        task["delayAmount"] = 5; task["delayUnit"] = "Hour";
        task["jobCountPerActor"] = 10;
        normalized = UnifiedDialogueProtocol.WithDefaults(task);
        Check((int)normalized["delayAmount"] == 5 && (int)normalized["jobCountPerActor"] == 10, "delay and count preserved");
        task["stopConditionType"] = "NeedLevel"; task["stopConditionDefName"] = "Food";
        Check((string)UnifiedDialogueProtocol.WithDefaults(task)["executionMode"] == "RepeatUntil", "explicit structured stop activates repetition");
        var chat = RimTalkJson.Parse("{\"actionDecision\":\"NoAction\",\"dialogueResponses\":[{\"name\":\"A\",\"text\":\"你好\"}],\"assignments\":[]}");
        Check(UnifiedDialogueProtocol.Dialogue(chat).Contains("你好"), "ordinary chat works");
        Reject(() => UnifiedDialogueProtocol.Dialogue(RimTalkJson.Parse("{\"actionDecision\":\"Schedule\",\"dialogueResponses\":[{\"name\":\"A\",\"text\":\"我去攻击\"}],\"assignments\":[]}")), "action decision without assignment");
        Reject(() => UnifiedDialogueProtocol.Dialogue(RimTalkJson.Parse("{\"dialogueResponses\":[],\"assignments\":[]}")), "empty dialogue");
        Reject(() => UnifiedDialogueProtocol.Dialogue(RimTalkJson.Parse("{\"dialogueResponses\":[{\"name\":\"A\",\"text\":\"Hi\"}]}")), "missing assignments");
        var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "work:NewModTask", "command:StandStill" };
        Check(UnifiedDialogueProtocol.DetailIds(RimTalkJson.Parse("{\"ids\":[\"work:NewModTask\",\"command:StandStill\"]}"), offered).Count == 2, "batch details include mod abilities");
        Reject(() => UnifiedDialogueProtocol.DetailIds(RimTalkJson.Parse("{\"ids\":[\"invented\"]}"), offered), "unknown detail ID");
        var call = new Dictionary<string, object> { ["id"] = "call_1", ["type"] = "function", ["function"] = new Dictionary<string, object> { ["name"] = UnifiedDialogueProtocol.FinishTool, ["arguments"] = RimTalkJson.Serialize(result) } };
        string response = RimTalkJson.Serialize(new Dictionary<string, object> { ["choices"] = new List<object> { new Dictionary<string, object> { ["finish_reason"] = "tool_calls", ["message"] = new Dictionary<string, object> { ["tool_calls"] = new List<object> { call } } } }, ["usage"] = new Dictionary<string, object> { ["total_tokens"] = 42 } });
        var message = UnifiedDialogueProtocol.Message(response, out var usage);
        var calls = UnifiedDialogueProtocol.Calls(message);
        Check(calls.Count == 1 && RimTalkJson.String(usage, "total_tokens") == "42", "tool completion and usage parsed");
        Check(UnifiedDialogueProtocol.Dialogue(UnifiedDialogueProtocol.Arguments(calls[0])) == dialogue, "full tool transport roundtrip");
        Check(UnifiedDialogueProtocol.Dialogue(UnifiedDialogueProtocol.ContentArguments(RimTalkJson.Parse("{\"content\":" + RimTalkJson.Serialize(RimTalkJson.Serialize(result)) + "}"))) == dialogue,
            "plain JSON content uses same validated dialogue schema");
        Reject(() => UnifiedDialogueProtocol.Message(response.Replace("\"finish_reason\":\"tool_calls\"", "\"finish_reason\":\"length\""), out _), "truncated response");
        Reject(() => UnifiedDialogueProtocol.Calls(RimTalkJson.Parse("{\"content\":\"我去做\"}")), "plain acceptance cannot fabricate tasks");
    }
}
