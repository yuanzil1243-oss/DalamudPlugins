using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;

namespace PoseKit.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    /// Every installed mod, cached since building it costs one IPC call per mod.
    private List<(string Directory, string Name, string? SortPath, bool Enabled)>? allModsCache;
    private List<string>? availableFoldersCache;
    private string modSearch = "";

    public ConfigWindow(Plugin plugin) : base($"PoseKit Settings v{Plugin.Version}###PoseKitConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 250),
            MaximumSize = new Vector2(900, 900),
        };
        Size = new Vector2(450, 450);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    /// Penumbra may not be ready right after login, so rebuild the cache on every open.
    public override void OnOpen() => InvalidateModCache();

    /// Rebuilds the mod list on the next draw, e.g. after Penumbra deleted or moved a mod.
    public void InvalidateModCache()
    {
        allModsCache = null;
        availableFoldersCache = null;
    }

    public override void Draw()
    {
        PoseKitUi.PaintWindowBackground();
        using var theme = PoseKitUi.PushTheme();

        if (ImGui.BeginChild("##PoseKitSettingsScroll", Vector2.Zero, false, ImGuiWindowFlags.None))
        {
            PoseKitUi.DrawDependencyStatus(plugin);

            PoseKitUi.SectionHeader("同步");
            var heelsLoaded = plugin.SimpleHeelsBridge.IsLoaded;
            var bridgeToHeels = configuration.BridgeOffsetToSimpleHeels;
            using (ImRaii.Disabled(!heelsLoaded))
            {
                if (ImGui.Checkbox("将偏移同步到 SimpleHeels", ref bridgeToHeels))
                {
                    configuration.BridgeOffsetToSimpleHeels = bridgeToHeels;
                    configuration.Save();
                }
            }
            PoseKitUi.TextWrappedDisabled(heelsLoaded
                ? "已检测到 SimpleHeels。"
                : "未检测到 SimpleHeels——请安装并启用它，以便将姿势偏移同步给附近玩家。");

            PoseKitUi.SectionHeader("扫描姿势的 Penumbra 模组");
            PoseKitUi.TextWrappedDisabled("禁用的模组也会列出——从中播放姿势时，PoseKit 会临时启用它。");

            if (allModsCache == null)
                RefreshAllMods();

            var folderFilter = configuration.PenumbraFolderFilter;
            PoseKitUi.DrawFolderFilterCombo("排序文件夹##PoseKitFolderFilter", configuration, availableFoldersCache);
            PoseKitUi.TextWrappedDisabled("按 Penumbra 自己的模组分类文件夹筛选（模组页），不是磁盘文件夹。");

            if (ImGui.Button("刷新模组##PoseKitRefreshMods"))
                RefreshAllMods();

            if (allModsCache == null)
            {
                ImGui.TextDisabled("未找到 Penumbra。");
            }
            else
            {
                var filteredMods = allModsCache
                    .Where(mod => MatchesFolderFilter(mod.SortPath, folderFilter))
                    .ToList();

                ImGui.SameLine();
                var selectedCount = filteredMods.Count(mod => configuration.SelectedPenumbraMods.Contains(mod.Directory));
                ImGui.TextColored(PoseKitUi.Accent,
                    $"已选择 {selectedCount} / 找到 {filteredMods.Count}");

                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##PoseKitModSearch", "搜索模组……", ref modSearch, 128);

                var visibleMods = filteredMods
                    .Where(mod => MatchesModSearch(mod, modSearch))
                    .OrderByDescending(mod => configuration.SelectedPenumbraMods.Contains(mod.Directory))
                    .ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var changed = false;
                if (ImGui.SmallButton("选择可见项##PoseKitSelectVisibleMods"))
                    foreach (var mod in visibleMods)
                        changed |= configuration.SelectedPenumbraMods.Add(mod.Directory);

                ImGui.SameLine();
                if (ImGui.SmallButton("清除可见项##PoseKitClearVisibleMods"))
                    foreach (var mod in visibleMods)
                        changed |= configuration.SelectedPenumbraMods.Remove(mod.Directory);

                ImGui.SameLine();
                ImGui.TextDisabled($"显示 {visibleMods.Count} 项");

                if (ImGui.BeginChild("##PoseKitModPicker", new Vector2(0, 210), true, ImGuiWindowFlags.None))
                {
                    foreach (var (modDirectory, modName, _, modEnabled) in visibleMods)
                    {
                        var isSelected = configuration.SelectedPenumbraMods.Contains(modDirectory);
                        if (ImGui.Checkbox($"{modName}##PoseKitModPick{modDirectory.GetHashCode()}", ref isSelected))
                        {
                            if (isSelected) configuration.SelectedPenumbraMods.Add(modDirectory);
                            else configuration.SelectedPenumbraMods.Remove(modDirectory);
                            changed = true;
                        }

                        if (!modEnabled)
                        {
                            ImGui.SameLine();
                            ImGui.TextDisabled("（已在 Penumbra 中禁用）");
                        }

                        if (!string.Equals(modName, modDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            ImGui.Indent();
                            ImGui.TextDisabled(modDirectory);
                            ImGui.Unindent();
                        }
                    }
                }
                ImGui.EndChild();

                changed |= DrawMissingMods();

                if (changed)
                {
                    configuration.Save();
                    plugin.RefreshPenumbraPoses();
                }
            }

            PoseKitUi.SectionHeader("支持");
            ImGui.TextUnformatted("Discord: raylapetal");
            ImGui.SameLine();
            if (ImGui.SmallButton("复制##PoseKitCopyDiscord"))
                ImGui.SetClipboardText("raylapetal");

            if (ImGui.Button("在 GitHub 报告问题##PoseKitGitHubIssues"))
                Util.OpenLink("https://github.com/RaylaPetal/PoseKit/issues");
            PoseKitUi.TextWrappedDisabled("欢迎提出问题和报告错误。");
        }
        ImGui.EndChild();
    }

    /// Selected mods Penumbra no longer has. The picker can't show them, so they get their own list
    /// to remove them from. Only called with the mod list loaded, so nothing is flagged while
    /// Penumbra is unavailable. Returns whether the selection changed.
    private bool DrawMissingMods()
    {
        var known = new HashSet<string>(allModsCache!.Select(mod => mod.Directory));
        var missing = configuration.SelectedPenumbraMods
            .Where(directory => !known.Contains(directory))
            .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count == 0) return false;

        if (!ImGui.CollapsingHeader($"缺少的模组 ({missing.Count})###PoseKitMissingMods")) return false;
        PoseKitUi.TextWrappedDisabled("已选择，但已不在 Penumbra 中（已删除或改名）。");

        var changed = false;
        if (ImGui.SmallButton("移除所有缺失项##PoseKitRemoveAllMissing"))
        {
            foreach (var directory in missing)
                changed |= configuration.SelectedPenumbraMods.Remove(directory);
            return changed;
        }

        foreach (var directory in missing)
        {
            if (ImGui.SmallButton($"移除##PoseKitRemoveMissing{directory.GetHashCode()}"))
                changed |= configuration.SelectedPenumbraMods.Remove(directory);
            ImGui.SameLine();
            ImGui.TextColored(PoseKitUi.Bad, directory);
        }

        return changed;
    }

    private void RefreshAllMods()
    {
        var modList = plugin.PenumbraIpc.TryGetModList();
        var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();
        if (modList == null || collectionId is not { } cid)
        {
            allModsCache = null;
            availableFoldersCache = null;
            return;
        }

        var mods = new List<(string, string, string?, bool)>();
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (directory, name) in modList)
        {
            var sortPath = plugin.PenumbraIpc.TryGetModPath(directory, name);
            PoseKitUi.AddSortFolders(sortPath, folders);

            var (enabled, _, _) = plugin.PenumbraIpc.TryGetCurrentSettings(cid, directory);
            mods.Add((directory, name, sortPath, enabled));
        }

        allModsCache = mods;
        availableFoldersCache = [.. folders];
    }

    private static bool MatchesFolderFilter(string? sortPath, string filter)
    {
        filter = filter.Trim().Trim('/');
        if (filter.Length == 0) return true;
        if (sortPath == null) return false;

        return sortPath.Equals(filter, StringComparison.OrdinalIgnoreCase)
            || sortPath.StartsWith(filter + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesModSearch((string Directory, string Name, string? SortPath, bool Enabled) mod, string search)
        => string.IsNullOrWhiteSpace(search)
           || mod.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
           || mod.Directory.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
}
