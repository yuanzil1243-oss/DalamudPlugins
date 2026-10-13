using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PoseKit.Windows;

/// <summary>
/// First-run tutorial, shown once.
/// </summary>
public class WelcomeWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private List<string>? availableFoldersCache;

    public WelcomeWindow(Plugin plugin) : base("欢迎使用 PoseKit###PoseKitWelcome")
    {
        Flags = ImGuiWindowFlags.NoCollapse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 420),
            MaximumSize = new Vector2(600, 800),
        };
        Size = new Vector2(460, 480);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void OnOpen() => availableFoldersCache = null;

    public override void Draw()
    {
        PoseKitUi.PaintWindowBackground();
        using var theme = PoseKitUi.PushTheme();

        ImGui.TextWrapped("PoseKit 可以调整、保存和重播姿势，自动发现 Penumbra 动画模组，还能通过 SimpleHeels 将姿势同步给已配对的伙伴。");

        PoseKitUi.SectionHeader("运行要求");
        PoseKitUi.DrawDependencyStatus(plugin);
        PoseKitUi.TextWrappedDisabled("发现动画模组需要 Penumbra。SimpleHeels 是可选的，仅在你希望将姿势偏移同步给已配对玩家时需要。");

        PoseKitUi.SectionHeader("快速开始");
        ImGui.TextWrapped("1. 先在下方设置文件夹筛选，再在“设置”中选择要扫描的模组。");
        ImGui.TextWrapped("2. 打开顶部的“动画”页，点击任意姿势的播放按钮。");
        ImGui.TextWrapped("3. 使用“实时偏移”调整角色位置，再在“预设”中保存为命名预设。");

        PoseKitUi.SectionHeader("动画模组文件夹筛选");
        if (availableFoldersCache == null)
            RefreshFolders();
        PoseKitUi.DrawFolderFilterCombo("##PoseKitWelcomeFolderFilter", configuration, availableFoldersCache);
        PoseKitUi.TextWrappedDisabled("将模组选择器限制在这个 Penumbra 分类文件夹中；你可以随时在“设置”里修改它以及要扫描的具体模组。");

        ImGui.Spacing();
        ImGui.Spacing();
        if (ImGui.Button("知道了，开始使用！##PoseKitWelcomeDone", new Vector2(ImGui.GetContentRegionAvail().X, 32)))
        {
            configuration.HasSeenWelcome = true;
            configuration.Save();
            IsOpen = false;
        }
    }

    /// Stays null while Penumbra is unavailable, so the next frame retries.
    private void RefreshFolders()
    {
        if (plugin.PenumbraIpc.TryGetModList() is not { } modList) return;

        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (directory, name) in modList)
            PoseKitUi.AddSortFolders(plugin.PenumbraIpc.TryGetModPath(directory, name), folders);

        availableFoldersCache = [.. folders];
    }
}
