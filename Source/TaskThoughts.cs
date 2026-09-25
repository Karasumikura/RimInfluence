using RimWorld;
using Verse;

namespace RimInfluence;

internal static class TaskThoughts
{
    public static void AddSuccess(Pawn pawn) => Add(pawn, "RimInfluence_TaskCompleted");
    public static void AddFailure(Pawn pawn) => Add(pawn, "RimInfluence_TaskFailed");

    private static void Add(Pawn pawn, string defName)
    {
        ThoughtDef thought = DefDatabase<ThoughtDef>.GetNamedSilentFail(defName);
        if (pawn?.needs?.mood?.thoughts?.memories != null && thought != null)
            pawn.needs.mood.thoughts.memories.TryGainMemory(thought);
    }
}
