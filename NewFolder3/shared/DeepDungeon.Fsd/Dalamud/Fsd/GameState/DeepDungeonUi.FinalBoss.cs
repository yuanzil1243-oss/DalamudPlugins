using DeepDungeon.Fsd.Core;
using FFXIVClientStructs.FFXIV.Component.GUI;
using global::Dalamud.Utility;

namespace DeepDungeon.Fsd.Dalamud.GameState;

public sealed partial class DeepDungeonUi
{
    public static unsafe bool TrySelectFinalBossChallenge(string dutyName, out string[] observed, out string error)
    {
        var labels = new List<string>();
        observed = [];
        error = "等待尾王挑战菜单。";
        if (!TryGetAddon("DeepDungeonMenu", out var menu)) return false;
        AtkComponentList* matchedList = null;
        int matchedIndex = -1, matches = 0;
        for (int n = 0; n < menu->UldManager.NodeListCount; n++)
        {
            var node = menu->UldManager.NodeList[n];
            var list = node == null ? null : node->GetAsAtkComponentList();
            if (list == null || list->IsUpdatePending || list->GetItemCount() is < 1 or > 32) continue;
            for (int i = 0; i < list->GetItemCount(); i++)
            {
                var renderer = list->GetItemRenderer(i);
                if (renderer == null || renderer->ListItemIndex != i) continue;
                var texts = new List<string>();
                for (int j = 0; j < renderer->UldManager.NodeListCount; j++)
                {
                    var child = renderer->UldManager.NodeList[j];
                    var text = child == null ? null : child->GetAsAtkTextNode();
                    if (text != null) texts.Add(text->NodeText.ExtractText());
                }
                string label = string.Join(" ", texts);
                labels.Add(label);
                if (!FinalBossPracticePolicy.MatchesChallenge(label, dutyName)) continue;
                matchedList = list; matchedIndex = i; matches++;
            }
        }
        observed = labels.ToArray();
        if (matches != 1 || matchedList == null)
        {
            error = matches > 1 ? "尾王入口不唯一，已停止选择。" : "未找到尾王练习入口；请确认已解锁「卓异的悲寂歼灭战」。";
            return false;
        }
        // Dispatch the selected native list row; never guess an Agent menu index.
        matchedList->DispatchItemEvent(matchedIndex, AtkEventType.ListItemClick);
        error = string.Empty;
        return true;
    }

    public static unsafe bool ConfirmWindowMatchesFinalBoss(string dutyName)
    {
        if (!TryGetAddon("ContentsFinderConfirm", out var confirm)) return false;
        for (int n = 0; n < confirm->UldManager.NodeListCount; n++)
        {
            var node = confirm->UldManager.NodeList[n];
            var text = node == null ? null : node->GetAsAtkTextNode();
            if (text != null && FinalBossPracticePolicy.MatchesChallenge(text->NodeText.ExtractText(), dutyName)) return true;
        }
        if (confirm->AtkValues == null) return false;
        for (int i = 0; i < confirm->AtkValuesCount; i++)
        {
            ref var value = ref confirm->AtkValues[i];
            if (value.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString &&
                FinalBossPracticePolicy.MatchesChallenge(value.String.ToString(), dutyName)) return true;
        }
        return false;
    }
}
