using Verse;

namespace RimInfluence;

public sealed class CapabilityCard : IExposable
{
    public string Id = "";
    public string ModId = "";
    public string Description = "";
    public string Target = "";
    public string Requirements = "";
    public string ExpectedJob = "";

    public void ExposeData()
    {
        Scribe_Values.Look(ref Id, "id", "");
        Scribe_Values.Look(ref ModId, "modId", "");
        Scribe_Values.Look(ref Description, "description", "");
        Scribe_Values.Look(ref Target, "target", "");
        Scribe_Values.Look(ref Requirements, "requirements", "");
        Scribe_Values.Look(ref ExpectedJob, "expectedJob", "");
    }
}
