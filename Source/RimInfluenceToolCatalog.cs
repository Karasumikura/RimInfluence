using System.Linq;

namespace RimInfluence;

internal static class RimInfluenceToolCatalog
{
    public static string Prompt => BuildPrompt();

    private static string BuildPrompt()
    {
        string defs = string.Join(", ", Verse.DefDatabase<Verse.JobDef>.AllDefsListForReading
            .Where(d => d != null && !string.IsNullOrEmpty(d.defName))
            .Select(d => d.defName).Distinct().OrderBy(x => x));
        return
        "\n\n[RimInfluence tools]\n" +
        "If the dialogue contains a future or requested Pawn action, you MUST emit exactly one machine-readable JSON object before any natural-language reply. " +
        "Use this exact shape (normal JSON quotes, no backslashes): " +
        "{\"tool\":\"resolve_rimworld_intent\",\"arguments\":{\"decision\":\"None or Schedule\",\"actor\":\"...\",\"capabilityId\":\"need:Eat or work:...\",\"actionDescription\":\"...\",\"targetDefName\":\"...\",\"targetQuery\":\"...\",\"delayAmount\":0,\"delayUnit\":\"Hour\",\"sourceDialogue\":\"...\"}}. " +
        "Put the JSON on its own line. Do not omit it or replace it with prose. " +
        "jobDefName must be selected from the runtime list below. Do not invent JobDef names. " +
        "Runtime JobDefs: [" + defs + "].";
    }
}
