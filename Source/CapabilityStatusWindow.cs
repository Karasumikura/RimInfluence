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
        Widgets.Label(new Rect(rect.x, rect.y, 230f, 30f), RimInfluenceUiText.T("能力状态", "Capabilities", "能力の状態"));
        _filter = Widgets.TextField(new Rect(rect.x + 230f, rect.y, rect.width - 230f, 30f), _filter);

        List<string> ids = CapabilityCatalog.Ids().Where(id => id != "none")
            .Where(id => string.IsNullOrWhiteSpace(_filter)
                || id.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0
                || RimInfluenceUiText.CapabilityDescription(id).IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        Rect viewport = new Rect(rect.x, rect.y + 38f, rect.width, rect.height - 38f);
        Rect content = new Rect(0f, 0f, viewport.width - 20f, ids.Count * 38f);
        Widgets.BeginScrollView(viewport, ref _scroll, content);
        for (int i = 0; i < ids.Count; i++)
        {
            float y = i * 38f;
            string id = ids[i];
            Rect row = new Rect(0f, y, content.width, 36f);
            Widgets.DrawHighlightIfMouseover(row);
            string detail = RimInfluenceUiText.CapabilityDescription(id);
            Widgets.Label(new Rect(8f, y + 6f, content.width - 205f, 26f), detail);
            Widgets.Label(new Rect(content.width - 190f, y + 6f, 180f, 26f), CapabilityAudit.IsConnected(id)
                ? RimInfluenceUiText.T("已接入", "Connected", "接続済み")
                : RimInfluenceUiText.T("未接入", "Not connected", "未接続"));
            TooltipHandler.TipRegion(row, detail + "\n" + id);
        }
        Widgets.EndScrollView();
    }
}
