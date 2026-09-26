using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace RimInfluence;

internal sealed class CapabilityStatusWindow : Window
{
    private Vector2 _scroll;
    private string _filter = "";

    public override Vector2 InitialSize => new Vector2(950f, 700f);

    public CapabilityStatusWindow()
    {
        doCloseX = true;
        absorbInputAroundWindow = true;
    }

    public override void DoWindowContents(Rect rect)
    {
        CapabilityAudit.EnsureInitialized(Find.World, Find.World?.GetComponent<RimInfluenceWorldComponent>()?.Tasks);
        Widgets.Label(new Rect(rect.x, rect.y, 230f, 30f), RimInfluenceUiText.T("能力状态", "Capabilities", "能力の状態") + " v" + RimInfluenceUiText.Version);
        _filter = Widgets.TextField(new Rect(rect.x + 230f, rect.y, rect.width - 230f, 30f), _filter);

        List<string> allIds = CapabilityCatalog.Ids().Where(id => id != "none").ToList();
        var descriptions = allIds.ToDictionary(id => id, RimInfluenceUiText.CapabilityDescription,
            StringComparer.OrdinalIgnoreCase);
        foreach (var group in allIds.GroupBy(id => descriptions[id], StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
            foreach (string id in group)
                descriptions[id] += " (" + id.Substring(id.IndexOf(':') + 1) + ")";
        List<string> ids = allIds
            .Where(id => string.IsNullOrWhiteSpace(_filter)
                || id.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0
                || descriptions[id].IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        Widgets.Label(new Rect(rect.x + rect.width - 250f, rect.y + 35f, 145f, 26f),
            RimInfluenceUiText.T("接入状态", "Connection", "接続状態"));
        Widgets.Label(new Rect(rect.x + rect.width - 100f, rect.y + 35f, 85f, 26f),
            RimInfluenceUiText.T("已启用", "Enabled", "有効"));
        Rect viewport = new Rect(rect.x, rect.y + 64f, rect.width, rect.height - 64f);
        Rect content = new Rect(0f, 0f, viewport.width - 20f, ids.Count * 38f);
        Widgets.BeginScrollView(viewport, ref _scroll, content);
        for (int i = 0; i < ids.Count; i++)
        {
            float y = i * 38f;
            string id = ids[i];
            Rect row = new Rect(0f, y, content.width, 36f);
            Widgets.DrawHighlightIfMouseover(row);
            string detail = descriptions[id];
            Widgets.Label(new Rect(8f, y + 6f, content.width - 250f, 26f), detail);
            bool connected = CapabilityAudit.IsConnected(id);
            Widgets.Label(new Rect(content.width - 235f, y + 6f, 135f, 26f), connected
                ? RimInfluenceUiText.T("已接入", "Connected", "接続済み")
                : RimInfluenceUiText.T("未接入", "Not connected", "未接続"));
            bool enabled = RimInfluenceMod.Settings.IsCapabilityEnabled(id);
            Widgets.Checkbox(new Vector2(content.width - 48f, y + 6f), ref enabled, 24f);
            if (enabled != RimInfluenceMod.Settings.IsCapabilityEnabled(id))
                RimInfluenceMod.Settings.SetCapabilityEnabled(id, enabled);
            TooltipHandler.TipRegion(row, detail + "\n" + id);
        }
        Widgets.EndScrollView();
    }
}
