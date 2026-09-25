using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace RimInfluence;

// Pure protocol handling: no pawn access, natural-language routing, or network calls.
internal static class UnifiedDialogueProtocol
{
    internal const string FinishTool = "resolve_rimworld_intent";
    internal const string DetailTool = "describe_rimworld_capabilities";

    internal static IDictionary<string, object> Message(string response, out IDictionary<string, object> usage)
    {
        var root = RimTalkJson.Parse(response) ?? throw new InvalidOperationException("Invalid completion JSON");
        usage = root.TryGetValue("usage", out object u) ? RimTalkJson.Object(u) : null;
        if (!root.TryGetValue("choices", out object c) || !(c is IList choices) || choices.Count == 0)
            throw new InvalidOperationException("Completion has no choices");
        var choice = RimTalkJson.Object(choices[0]);
        string reason = RimTalkJson.String(choice, "finish_reason");
        if (reason == "length" || reason == "content_filter")
            throw new InvalidOperationException("Incomplete model response: " + reason);
        return choice != null && choice.TryGetValue("message", out object m) && RimTalkJson.Object(m) != null
            ? RimTalkJson.Object(m) : throw new InvalidOperationException("Completion has no message");
    }

    internal static IList Calls(IDictionary<string, object> message)
    {
        if (!message.TryGetValue("tool_calls", out object value) || !(value is IList calls) || calls.Count == 0)
            throw new InvalidOperationException("Model returned no structured dialogue/action tool call");
        return calls;
    }

    internal static IDictionary<string, object> Function(object call)
    {
        var data = RimTalkJson.Object(call);
        return data != null && data.TryGetValue("function", out object f) && RimTalkJson.Object(f) != null
            ? RimTalkJson.Object(f) : throw new InvalidOperationException("Malformed tool call");
    }

    internal static IDictionary<string, object> Arguments(object call)
        => RimTalkJson.Parse(RimTalkJson.String(Function(call), "arguments"))
            ?? throw new InvalidOperationException("Malformed tool arguments");

    internal static string Dialogue(IDictionary<string, object> result)
    {
        if (!result.TryGetValue("dialogueResponses", out object value) || !(value is IList responses) || responses.Count == 0)
            throw new InvalidOperationException("Final tool omitted dialogueResponses");
        foreach (object response in responses)
        {
            var item = RimTalkJson.Object(response);
            if (string.IsNullOrWhiteSpace(RimTalkJson.String(item, "name")) || string.IsNullOrWhiteSpace(RimTalkJson.String(item, "text")))
                throw new InvalidOperationException("Dialogue requires a speaker and non-empty text");
        }
        if (!result.TryGetValue("assignments", out object assignments) || !(assignments is IList))
            throw new InvalidOperationException("Final tool omitted assignments array");
        string decision = RimTalkJson.String(result, "actionDecision");
        if (decision != "NoAction" && decision != "Schedule" && decision != "Cancel" && decision != "Mixed")
            throw new InvalidOperationException("Final tool omitted actionDecision");
        int count = ((IList)assignments).Count;
        if ((decision == "NoAction") != (count == 0))
            throw new InvalidOperationException("Action decision disagrees with assignment count");
        // Keep act, target and additional RimTalk response properties intact.
        return RimTalkJson.Serialize(responses);
    }

    internal static Dictionary<string, object> WithDefaults(IDictionary<string, object> assignment)
    {
        var result = new Dictionary<string, object>(assignment, StringComparer.OrdinalIgnoreCase);
        if (!result.ContainsKey("delayAmount")) result["delayAmount"] = 0;
        if (!result.ContainsKey("delayUnit")) result["delayUnit"] = "Hour";
        if (!result.ContainsKey("executionMode"))
        {
            string stop = RimTalkJson.String(result, "stopConditionType");
            result["executionMode"] = !string.IsNullOrEmpty(stop) && stop != "None" ? "RepeatUntil" : "Once";
        }
        return result;
    }

    internal static List<string> DetailIds(IDictionary<string, object> args, ISet<string> offered)
    {
        if (!args.TryGetValue("ids", out object value) || !(value is IList ids) || ids.Count == 0)
            throw new InvalidOperationException("Capability details require at least one ID");
        var result = ids.Cast<object>().Select(id => id as string).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (result.Any(id => id == null || !offered.Contains(id)))
            throw new InvalidOperationException("Unknown capability in detail request");
        return result;
    }
}
