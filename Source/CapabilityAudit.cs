using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimInfluence;

internal static class CapabilityAudit
{
    private sealed class Contract
    {
        public string Id;
        public string Kind;
        public string Problem;
    }

    public static string ReportPath => Path.Combine(GenFilePaths.ConfigFolderPath, "RimInfluence-Capability-Audit.txt");
    public static string Summary { get; private set; } = "尚未核验。";
    private static World _lastWorld;
    private static HashSet<string> _broken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static bool IsStructurallyAvailable(string id) => !_broken.Contains(id);

    public static bool IsConnected(string id) => !_broken.Contains(id);

    public static string ProblemFor(string id) => _problems.TryGetValue(id, out string problem) ? problem : null;

    public static void EnsureInitialized(World world, IEnumerable<ScheduledTask> tasks)
    {
        if (ReferenceEquals(_lastWorld, world)) return;
        _lastWorld = world;
        Refresh(tasks);
    }

    public static void Refresh(IEnumerable<ScheduledTask> tasks)
    {
        try
        {
            List<ScheduledTask> history = tasks?.Where(task => task != null).ToList() ?? new List<ScheduledTask>();
            List<Contract> contracts = CapabilityCatalog.Ids().Where(id => id != "none")
                .Select(CheckContract).OrderBy(result => result.Id, StringComparer.OrdinalIgnoreCase).ToList();
            _broken = new HashSet<string>(contracts.Where(result => result.Problem != null).Select(result => result.Id),
                StringComparer.OrdinalIgnoreCase);
            _problems = contracts.Where(result => result.Problem != null)
                .ToDictionary(result => result.Id, result => result.Problem, StringComparer.OrdinalIgnoreCase);
            int structuralFailures = contracts.Count(result => result.Problem != null);
            int observedSuccesses = contracts.Count(result => history.Any(task => task.CapabilityId == result.Id && task.CompletedIterations > 0));
            int observedFailures = contracts.Count(result => history.Any(task => task.CapabilityId == result.Id && task.Status == ScheduledTaskStatus.Failed));
            Summary = RimInfluenceUiText.T($"已接入 {contracts.Count - structuralFailures} / {contracts.Count} 项能力",
                $"Connected capabilities: {contracts.Count - structuralFailures} / {contracts.Count}",
                $"接続済みの能力: {contracts.Count - structuralFailures} / {contracts.Count}");

            var report = new StringBuilder();
            report.AppendLine("RimInfluence 能力自动核验");
            report.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine(Summary);
            report.AppendLine("结构通过只表示执行入口存在；原生 Job 成功只验证当时的具体目标和条件，不保证其他存档或其他 Mod 组合。扫描不派发任何 Job。");
            report.AppendLine("实战记录来自当前存档中 RimInfluence 保存的任务。失败可能是游戏条件不满足，并不自动说明执行器损坏。");
            report.AppendLine();
            foreach (Contract contract in contracts)
            {
                List<ScheduledTask> runs = history.Where(task => string.Equals(task.CapabilityId, contract.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                int nativeSuccesses = runs.Sum(task => task.CompletedIterations);
                int started = runs.Count(task => !string.IsNullOrWhiteSpace(task.LastJobDefName));
                int failures = runs.Count(task => task.Status == ScheduledTaskStatus.Failed);
                int completedPlans = runs.Count(task => task.Status == ScheduledTaskStatus.Completed && task.CompletedIterations > 0);
                string status = contract.Problem != null ? "结构异常"
                    : nativeSuccesses > 0 ? "有实战成功记录" : "待实战验证";
                report.AppendLine($"[{status}] {contract.Id} ({contract.Kind})");
                if (contract.Problem != null) report.AppendLine("  结构问题：" + contract.Problem);
                report.AppendLine($"  任务数={runs.Count} Job已启动={started} 原生成功次数={nativeSuccesses} 已完成计划={completedPlans} 失败计划={failures}");
                foreach (ScheduledTask failed in runs.Where(task => task.Status == ScheduledTaskStatus.Failed).Reverse().Take(3))
                    report.AppendLine($"  最近失败：job={failed.LastJobDefName} 目标={failed.TargetQuery} 原因={failed.FailureReason}");
                string jobs = string.Join(", ", runs.Select(task => task.LastJobDefName)
                    .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase));
                if (jobs.Length > 0) report.AppendLine("  实际 JobDef：" + jobs);
            }
            File.WriteAllText(ReportPath, report.ToString(), new UTF8Encoding(true));
            Log.Message($"[RimInfluence] capability audit: {Summary} report={ReportPath}");
        }
        catch (Exception ex)
        {
            Summary = "核验失败：" + ex.GetBaseException().Message;
            Log.Warning("[RimInfluence] capability audit failed: " + ex);
        }
    }

    private static Contract CheckContract(string id)
    {
        var result = new Contract { Id = id, Kind = "registered" };
        try
        {
            if (id.StartsWith("work:", StringComparison.OrdinalIgnoreCase))
            {
                WorkGiverDef def = DefDatabase<WorkGiverDef>.GetNamedSilentFail(id.Substring(5));
                if (def?.giverClass == null || !typeof(WorkGiver).IsAssignableFrom(def.giverClass))
                    throw new InvalidOperationException("WorkGiver 类不存在或类型不兼容");
                WorkGiver worker = def.Worker;
                if (worker == null) throw new InvalidOperationException("WorkGiver 无法实例化");
                if (worker is WorkGiver_Scanner)
                {
                    result.Kind = "scanner";
                    bool things = Overrides(def.giverClass, "JobOnThing", typeof(WorkGiver_Scanner), typeof(Pawn), typeof(Thing), typeof(bool));
                    bool cells = Overrides(def.giverClass, "JobOnCell", typeof(WorkGiver_Scanner), typeof(Pawn), typeof(IntVec3), typeof(bool));
                    if (!things && !cells) throw new InvalidOperationException("当前执行器只调用 JobOnThing/JobOnCell，但该 WorkGiver 两者均未实现");
                    result.Kind += things && cells ? ":thing+cell" : things ? ":thing" : ":cell";
                }
                else
                {
                    result.Kind = "non-scanner";
                    if (!Overrides(def.giverClass, "NonScanJob", typeof(WorkGiver), typeof(Pawn)))
                        throw new InvalidOperationException("当前执行器调用 NonScanJob，但该 WorkGiver 未实现");
                }
            }
            else if (id.Equals("need:Eat", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = "need";
                if (typeof(JobGiver_GetFood).GetMethod("TryGiveJob", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(Pawn) }, null) == null)
                    throw new MissingMethodException("原生食物 JobGiver 入口不存在");
            }
            else if (id.Equals("interaction:Ingest", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = "interaction";
                if (JobDefOf.Ingest == null) throw new MissingMethodException("原生摄取 JobDef 不存在");
            }
            else if (id.Equals("interaction:Arrest", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = "interaction";
                if (!NativeInteractionExecutor.Available || JobDefOf.Arrest == null)
                    throw new MissingMethodException("原生拘捕交互或 Arrest JobDef 不存在");
            }
            else if (id.Equals("interaction:Attack", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = "interaction";
                if (JobDefOf.AttackMelee == null || JobDefOf.AttackStatic == null)
                    throw new MissingMethodException("原生攻击 JobDef 不存在");
            }
            else if (id.Equals("command:StandStill", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = "command";
                if (JobDefOf.Wait == null) throw new MissingMethodException("原地等待 JobDef 不存在");
            }
            else if (id.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = "legacy";
                if (!Enum.TryParse(id.Substring(7), true, out InfluenceAction action)
                    || !InfluenceActionExecutor.SupportsLegacy(action))
                    throw new InvalidOperationException("没有对应的传统动作执行分支");
            }
            else throw new InvalidOperationException("未知能力类型");
        }
        catch (Exception ex) { result.Problem = ex.GetBaseException().Message; }
        return result;
    }

    private static bool Overrides(Type type, string name, Type baseType, params Type[] parameters)
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, parameters, null);
        return method != null && method.DeclaringType != baseType;
    }
}
