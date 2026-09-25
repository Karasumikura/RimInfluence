using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using UnityEngine.Networking;
using Verse;

namespace RimInfluence;

internal static class ModCapabilityImporter
{
    public static bool Busy { get; private set; }
    public static string Status { get; private set; } = RimInfluenceUiText.T("尚未扫描新 Mod。", "New mod capabilities have not been scanned.", "新しいModの能力はまだ確認していません。");

    public static void Start()
    {
        if (Busy) return;
        Busy = true;
        Status = RimInfluenceUiText.T("正在扫描已加载 Mod 的工作能力...", "Scanning loaded mod work capabilities...", "導入済みModの仕事能力を確認中...");
        _ = GenerateAsync();
    }

    private static async Task GenerateAsync()
    {
        try
        {
            if (Current.Game == null)
            {
                Status = RimInfluenceUiText.T("请先进入存档，再生成能力说明。", "Load a save before generating capability descriptions.", "セーブを読み込んでから能力の説明を生成してください。");
                return;
            }
            CapabilityAudit.Refresh(Find.World?.GetComponent<RimInfluenceWorldComponent>()?.Tasks);
            var known = new HashSet<string>(RimInfluenceMod.Settings.Cards.Select(card => card?.Id ?? ""), StringComparer.OrdinalIgnoreCase);
            var groups = DefDatabase<WorkGiverDef>.AllDefsListForReading
                .Where(def => def?.giverClass != null && def.modContentPack != null)
                .Where(def => CapabilityAudit.IsStructurallyAvailable("work:" + def.defName))
                .Where(def => !known.Contains("work:" + def.defName))
                .GroupBy(def => def.modContentPack.PackageId)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key)
                    && !group.Key.StartsWith("ludeon.", StringComparison.OrdinalIgnoreCase)
                    && !group.Key.Equals("karasumikura.riminfluence", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (groups.Count == 0)
            {
                Status = RimInfluenceUiText.T("没有发现需要生成说明的新增工作能力。", "No new work capabilities need descriptions.", "説明が必要な新しい仕事能力はありません。");
                return;
            }
            object client = await GetRimTalkClient();
            if (client == null) throw new InvalidOperationException("当前 RimTalk 模型未配置或不支持此接口");
            MethodInfo send = AccessTools.Method(client.GetType(), "SendRequestAsync", new[] { typeof(string), typeof(DownloadHandler) });
            if (send == null) throw new InvalidOperationException("当前 RimTalk 提供方无法发起收录请求");
            int imported = 0;
            int failedBatches = 0;
            foreach (var group in groups)
            {
                var defs = group.OrderBy(def => def.defName).ToList();
                for (int offset = 0; offset < defs.Count; offset += 20)
                {
                    List<WorkGiverDef> batch = defs.Skip(offset).Take(20).ToList();
                    string progress = $"{group.Key}: {Math.Min(offset + 20, defs.Count)}/{defs.Count}";
                    Status = RimInfluenceUiText.T("正在生成能力说明 " + progress, "Generating capability descriptions " + progress,
                        "能力の説明を生成中 " + progress);
                    try
                    {
                        string prompt = "Generate concise Chinese and English searchable capability metadata for these loaded RimWorld WorkGiverDefs. "
                            + "Return JSON only: {\"cards\":[{\"id\":\"work:exactDefName\",\"description\":\"what it does\",\"target\":\"target type\",\"requirements\":\"likely game constraints\",\"expectedJob\":\"known JobDef name or empty\"}]}. "
                            + "Do not invent abilities or executable code. Unknown prerequisites and JobDef must be empty. These descriptions help search; game rules remain authoritative.\n"
                            + string.Join("\n", batch.Select(def => "work:" + def.defName + " | verb=" + def.verb
                                + " | gerund=" + def.gerund + " | class=" + def.giverClass.FullName
                                + " | workType=" + (def.workType?.label ?? "")));
                        string json = RimTalkJson.Serialize(new Dictionary<string, object>
                        {
                            ["model"] = ActiveModel(),
                            ["stream"] = false,
                            ["messages"] = new List<object> { new Dictionary<string, object> { ["role"] = "user", ["content"] = prompt } }
                        });
                        string response = await (Task<string>)send.Invoke(client, new object[] { json, new DownloadHandlerBuffer() });
                        IDictionary<string, object> root = RimTalkJson.Parse(response);
                        IDictionary<string, object> choice = FirstChoice(root);
                        IDictionary<string, object> message = choice != null && choice.TryGetValue("message", out object msg) ? RimTalkJson.Object(msg) : null;
                        string content = RimTalkJson.String(message, "content").Trim();
                        if (content.StartsWith("```", StringComparison.Ordinal))
                            content = content.Replace("```json", "").Replace("```", "").Trim();
                        IDictionary<string, object> result = RimTalkJson.Parse(content);
                        if (result == null || !result.TryGetValue("cards", out object cardsValue) || !(cardsValue is IEnumerable cards))
                            throw new InvalidOperationException("模型没有返回可读取的能力卡");
                        foreach (object item in cards)
                        {
                            IDictionary<string, object> cardData = RimTalkJson.Object(item);
                            string id = RimTalkJson.String(cardData, "id");
                            if (!batch.Any(def => id.Equals("work:" + def.defName, StringComparison.OrdinalIgnoreCase)) || known.Contains(id)) continue;
                            string description = Limit(RimTalkJson.String(cardData, "description"), 180);
                            if (string.IsNullOrWhiteSpace(description)) continue;
                            string job = RimTalkJson.String(cardData, "expectedJob");
                            if (!string.IsNullOrWhiteSpace(job) && DefDatabase<JobDef>.GetNamedSilentFail(job) == null) job = "";
                            RimInfluenceMod.Settings.Cards.Add(new CapabilityCard
                            {
                                Id = id, ModId = group.Key, Description = description,
                                Target = Limit(RimTalkJson.String(cardData, "target"), 100),
                                Requirements = Limit(RimTalkJson.String(cardData, "requirements"), 180),
                                ExpectedJob = job
                            });
                            known.Add(id);
                            imported++;
                        }
                        RimInfluenceMod.Settings.Write();
                    }
                    catch (Exception ex)
                    {
                        failedBatches++;
                        Log.Warning($"[RimInfluence] mod capability import failed mod={group.Key}: {ex.GetBaseException().Message}");
                    }
                }
            }
            Status = RimInfluenceUiText.T($"收录完成：新增 {imported} 项，失败批次 {failedBatches}。",
                $"Descriptions added: {imported}. Failed batches: {failedBatches}.",
                $"説明を追加: {imported} 件。失敗したグループ: {failedBatches} 件。");
            Log.Message("[RimInfluence] " + Status);
        }
        catch (Exception ex)
        {
            Status = RimInfluenceUiText.T("能力说明生成失败：", "Could not generate capability descriptions: ",
                "能力の説明を生成できませんでした: ") + ex.GetBaseException().Message;
            Log.Warning("[RimInfluence] " + Status);
        }
        finally { Busy = false; }
    }

    private static async Task<object> GetRimTalkClient()
    {
        Type factory = AccessTools.TypeByName("RimTalk.Client.AIClientFactory");
        Task task = factory?.GetMethod("GetAIClientAsync", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as Task;
        if (task == null) return null;
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static string ActiveModel()
    {
        Type settingsType = AccessTools.TypeByName("RimTalk.Settings");
        object settings = settingsType?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        object config = settings?.GetType().GetMethod("GetActiveConfig")?.Invoke(settings, null);
        return config?.GetType().GetMethod("GetEffectiveModelName")?.Invoke(config, null) as string ?? "";
    }

    private static IDictionary<string, object> FirstChoice(IDictionary<string, object> root)
    {
        return root != null && root.TryGetValue("choices", out object value) && value is IList choices && choices.Count > 0
            ? RimTalkJson.Object(choices[0]) : null;
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value.Substring(0, length);
}
