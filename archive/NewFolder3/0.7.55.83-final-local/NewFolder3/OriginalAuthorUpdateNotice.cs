using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace NewFolder3;

internal sealed class OriginalAuthorUpdateNotice(OriginalAuthorUpdateNoticeState state)
{
    private const string Title = "原作者版本已更新###NewFolder3OriginalAuthorUpdate";

    internal void Draw()
    {
        if (state.Acknowledged)
            return;

        // Reopen after external dismissal. Only the confirmation button acknowledges the notice.
        if (!ImGui.IsPopupOpen(Title))
            ImGui.OpenPopup(Title);
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * .5f, ImGuiCond.Appearing, new Vector2(.5f));
        ImGui.SetNextWindowSize(new Vector2(700f * ImGui.GetIO().FontGlobalScale, 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(Title, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
            return;
        try
        {
            ImGui.TextWrapped(OriginalAuthorUpdateNoticeState.Recommendation);
            ImGui.Spacing();
            ImGui.TextWrapped("当前仍是基于 .82-final-local 的本地修改版。点击确定只会允许继续使用本地版，不会切换版本或自动启动任务。");
            ImGui.Spacing();
            ImGui.TextUnformatted("原作者库链（可选中复制）：");
            string feed = OriginalAuthorUpdateNoticeState.FeedUrl;
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##OriginalAuthorFeed", ref feed, 512,
                ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AutoSelectAll);
            if (ImGui.Button("复制原作者库链"))
                ImGui.SetClipboardText(OriginalAuthorUpdateNoticeState.FeedUrl);
            ImGui.Spacing();
            ImGui.TextUnformatted("原作者仓库（可选中复制）：");
            string repository = OriginalAuthorUpdateNoticeState.RepositoryUrl;
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##OriginalAuthorRepository", ref repository, 512,
                ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AutoSelectAll);
            if (ImGui.Button("复制原作者仓库"))
                ImGui.SetClipboardText(OriginalAuthorUpdateNoticeState.RepositoryUrl);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            if (ImGui.Button("确定##AcknowledgeOriginalAuthorUpdate", new Vector2(120, 0)))
            {
                state.Acknowledge();
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }
}
