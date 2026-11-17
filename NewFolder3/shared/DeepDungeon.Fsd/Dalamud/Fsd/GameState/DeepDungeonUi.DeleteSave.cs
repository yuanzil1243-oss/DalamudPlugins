using DeepDungeon.Fsd.Core;
using FFXIVClientStructs.FFXIV.Component.GUI;
using global::Dalamud.Utility;

namespace DeepDungeon.Fsd.Dalamud.GameState;

public sealed partial class DeepDungeonUi
{
    public static unsafe bool TrySelectResetProgress(out string[] observed, out string error)
    {
        var labels = new List<string>();
        var lists = new List<nint>();
        var indices = new List<int>();
        observed = [];
        error = "等待重置攻略进度菜单。";
        if (!TryGetAddon("DeepDungeonMenu", out var menu)) return false;
        for (int n = 0; n < menu->UldManager.NodeListCount; n++)
        {
            var node = menu->UldManager.NodeList[n];
            var list = node == null ? null : node->GetAsAtkComponentList();
            if (list == null || list->IsUpdatePending || list->GetItemCount() is < 1 or > 32) continue;
            for (int i = 0; i < list->GetItemCount(); i++)
            {
                string label = list->GetItemLabel(i).ToString();
                if (string.IsNullOrWhiteSpace(label))
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
                    label = string.Join(" ", texts);
                }
                labels.Add(label);
                lists.Add((nint)list);
                indices.Add(i);
            }
        }
        observed = labels.ToArray();
        int selected = DeepDungeonDeleteMenuPolicy.FindResetProgress(observed);
        if (selected < 0)
        {
            error = "重置攻略进度选项尚未读取或不唯一；未选择任何菜单项。";
            return false;
        }
        // Dispatch the observed native row, preserving its current unlock-dependent mapping.
        ((AtkComponentList*)lists[selected])->DispatchItemEvent(indices[selected], AtkEventType.ListItemClick);
        error = string.Empty;
        return true;
    }
}
