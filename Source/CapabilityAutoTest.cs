using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimInfluence;

[StaticConstructorOnStartup]
internal static class CapabilityAutoTest
{
    private const string SaveName = "autostart";
    private static readonly List<string> Results = new List<string>();
    private static readonly List<string> AttemptReasons = new List<string>();
    private static List<string> _ids;
    private static List<Pawn> _candidates;
    private static Job _activeJob;
    private static Pawn _pawn;
    private static string _activeId;
    private static int _startedTick;
    private static int _nextTick;
    private static int _index;
    private static int _candidateIndex;
    private static bool _finished;
    private static bool _started;

    public static string ReportPath => Path.Combine(GenFilePaths.ConfigFolderPath, "RimInfluence-AutoTest-Results.txt");
    private static bool Requested => GenCommandLine.CommandLineArgPassed("riminfluenceautotest")
        && GenCommandLine.TryGetCommandLineArg("savedatafolder", out string folder)
        && string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(GenFilePaths.SaveDataFolderPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
        && File.Exists(Path.Combine(folder, "RimInfluence-AutoTest.marker"))
        && File.Exists(GenFilePaths.FilePathForSavedGame(SaveName));

    // Returns true while the isolated run owns the world, suppressing saved dialogue tasks.
    public static bool Tick(World world)
    {
        if (!Requested || _finished) return Requested;
        if (Find.World != world || Find.CurrentMap == null || Current.ProgramState != ProgramState.Playing) return true;
        if (!_started)
        {
            _candidates = Find.Maps.SelectMany(map => map.mapPawns.FreeColonistsSpawned)
                .Where(p => p?.jobs != null && !p.Downed && !p.Dead).Distinct().ToList();
            if (_candidates.Count == 0) { Finish("No available colonist in test save."); return true; }
            _ids = CapabilityCatalog.Ids().Where(CapabilityCatalog.IsKnown).ToList();
            _started = true;
            _nextTick = GenTicks.TicksGame + 60;
            Results.Add("RimInfluence isolated native Job auto-test");
            Results.Add("Save copy: " + SaveName + "; eligible pawns: "
                + string.Join(", ", _candidates.Select(p => p.LabelShort)) + "; abilities: " + _ids.Count);
            Results.Add("PASS means a native Job returned Succeeded. NOT_VERIFIED means no tested pawn completed it; reasons are observations, not proof of missing map targets.");
            WriteReport();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            Log.Message($"[RimInfluence] auto-test started pawns={_candidates.Count} capabilities={_ids.Count}");
        }
        if (GenTicks.TicksGame < _nextTick) return true;
        if (_activeJob != null)
        {
            bool active = _pawn.jobs?.curJob == _activeJob;
            bool queued = _pawn.jobs?.jobQueue?.Contains(_activeJob) == true;
            if (GenTicks.TicksGame - _startedTick < (active ? 5000 : 3000) && (active || queued)) return true;
            string outcome = active ? "TIMEOUT" : queued ? "QUEUE_TIMEOUT" : "LOST_WITHOUT_OUTCOME";
            Record(outcome, "No tracked native success; pawn=" + _pawn.LabelShort);
            AttemptReasons.Add(_pawn.LabelShort + ": " + outcome);
            Job timedOutJob = _activeJob;
            _activeJob = null;
            _activeId = null;
            if (_pawn.jobs?.curJob == timedOutJob) _pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            else _pawn.jobs?.jobQueue?.RemoveAll(_pawn, job => ReferenceEquals(job, timedOutJob));
            _candidateIndex++;
            _nextTick = GenTicks.TicksGame + 30;
            return true;
        }
        if (_index >= _ids.Count) { Finish("Completed all registered capabilities."); return true; }
        if (_candidateIndex >= _candidates.Count)
        {
            Results.Add($"NOT_VERIFIED | {_ids[_index]} | " + string.Join("; ", AttemptReasons));
            WriteReport();
            Advance();
            return true;
        }
        string id = _ids[_index];
        _pawn = _candidates[_candidateIndex];
        if (_pawn.Downed || _pawn.Dead || _pawn.Map == null)
        {
            AttemptReasons.Add(_pawn.LabelShort + ": unavailable");
            _candidateIndex++;
            return true;
        }
        if (_candidateIndex == 0) PrepareNativeWorkFixture(id, _pawn);
        var task = new ScheduledTask
        {
            Id = "auto-test-" + _index + "-" + _candidateIndex,
            Pawn = _pawn,
            CapabilityId = id,
            TargetDefName = "",
            ExecuteAtTick = GenTicks.TicksGame,
            SourceDialogue = "Isolated capability auto-test"
        };
        if (id == "interaction:Ingest")
        {
            Thing ingestible = _pawn.Map.listerThings.AllThings.FirstOrDefault(t => t.Spawned
                && t.def?.ingestible != null
                && !t.IsForbidden(_pawn) && _pawn.CanReserve(t)
                && _pawn.CanReach(t, PathEndMode.Touch, Danger.Some));
            task.TargetDefName = ingestible?.def.defName ?? "";
        }
        if (id == "command:StandStill")
        {
            task.ExecutionMode = TaskExecutionMode.RepeatUntil;
            task.StopConditionType = TaskStopConditionType.ElapsedTime;
            task.StopAtTick = GenTicks.TicksGame + 120;
        }
        try
        {
            if (!InfluenceActionExecutor.TryStart(task))
            {
                AttemptReasons.Add(_pawn.LabelShort + ": " + Clean(task.LastExecutionError));
                _candidateIndex++;
                _nextTick = GenTicks.TicksGame + 1;
                return true;
            }
            _activeJob = task.ActiveJob ?? _pawn.jobs?.curJob;
            if (_activeJob == null)
            {
                Results.Add($"INVALID_START | {id} | executor returned true without an active Job");
                AttemptReasons.Add(_pawn.LabelShort + ": executor returned true without an active Job");
                WriteReport();
                _candidateIndex++;
                return true;
            }
            _activeId = id;
            _startedTick = GenTicks.TicksGame;
            _nextTick = GenTicks.TicksGame + 1;
            Results.Add($"DISPATCHED | {id} | pawn={_pawn.LabelShort} | job={_activeJob.def?.defName} | active={_pawn.jobs?.curJob == _activeJob} | target={Clean(_activeJob.targetA.Thing?.LabelShort ?? _activeJob.targetA.Cell.ToString())}");
            WriteReport();
        }
        catch (Exception ex)
        {
            Results.Add($"EXCEPTION | {id} | {Clean(ex.GetBaseException().Message)}");
            AttemptReasons.Add(_pawn.LabelShort + ": exception " + Clean(ex.GetBaseException().Message));
            WriteReport();
            _candidateIndex++;
        }
        return true;
    }

    public static void JobEnded(Pawn pawn, Job job, JobCondition condition)
    {
        if (!Requested || _activeJob == null || pawn != _pawn || !ReferenceEquals(job, _activeJob)) return;
        Record(condition == JobCondition.Succeeded ? "PASS" : "JOB_FAILED", "condition=" + condition);
        if (condition == JobCondition.Succeeded) Advance();
        else
        {
            AttemptReasons.Add(_pawn.LabelShort + ": native condition=" + condition);
            _candidateIndex++;
        }
        _activeJob = null;
        _activeId = null;
        _nextTick = GenTicks.TicksGame + 30;
    }

    private static void Advance()
    {
        _index++;
        _candidateIndex = 0;
        AttemptReasons.Clear();
    }

    private static void Record(string outcome, string detail)
    {
        Results.Add($"{outcome} | {_activeId} | pawn={_pawn?.LabelShort} | job={_activeJob?.def?.defName} | {detail}");
        WriteReport();
    }

    private static void Finish(string reason)
    {
        _finished = true;
        Results.Add("FINISHED | " + reason);
        WriteReport();
        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        Log.Message("[RimInfluence] auto-test finished; report=" + ReportPath);
    }

    private static void WriteReport()
    {
        File.WriteAllLines(ReportPath, Results, new UTF8Encoding(true));
    }

    private static void PrepareNativeWorkFixture(string id, Pawn pawn)
    {
        Map map = pawn.Map;
        if (id == "work:PlantsCut")
        {
            Plant tree = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>()
                .FirstOrDefault(p => p.Spawned && p.def?.plant?.IsTree == true && !p.IsForbidden(pawn)
                    && map.designationManager.DesignationOn(p, DesignationDefOf.CutPlant) == null);
            if (tree != null)
            {
                map.designationManager.AddDesignation(new Designation(tree, DesignationDefOf.CutPlant));
                Results.Add("FIXTURE | " + id + " | designated " + tree.LabelShort);
            }
        }
        else if (id == "work:Mine")
        {
            Mineable rock = map.listerThings.AllThings.OfType<Mineable>()
                .FirstOrDefault(t => t.Spawned && pawn.CanReach(t, PathEndMode.Touch, Danger.Some)
                    && map.designationManager.DesignationOn(t, DesignationDefOf.Mine) == null);
            if (rock != null)
            {
                map.designationManager.AddDesignation(new Designation(rock, DesignationDefOf.Mine));
                Results.Add("FIXTURE | " + id + " | designated " + rock.LabelShort);
            }
        }
        else if (id == "work:CleanFilth")
        {
            bool made = FilthMaker.TryMakeFilth(pawn.Position, map, ThingDefOf.Filth_Dirt);
            Results.Add("FIXTURE | " + id + " | dirt created=" + made);
        }
        WriteReport();
    }

    private static string Clean(string value) => (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
}
