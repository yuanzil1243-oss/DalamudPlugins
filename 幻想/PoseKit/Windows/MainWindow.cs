using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace PoseKit.Windows;

/// <summary>
/// Header with page tabs and tools, then the selected page beside a right rail with Pairing, Live
/// Offset and Bone Align.
/// </summary>
public class MainWindow : Window, IDisposable
{
    private enum Page { Animations, Presets }

    private const float RailWidth = 300f;
    private const float NarrowRailWidth = 260f;
    private const float NarrowWindowWidth = 820f;

    private readonly Plugin plugin;
    private Page page = Page.Animations;

    public MainWindow(Plugin plugin)
        : base($"PoseKit v{Plugin.Version}###MainWindow")
    {
        // The window itself stays fixed; each card scrolls on its own.
        Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(680, 460),
            MaximumSize = new Vector2(1600, 1200)
        };

        Size = new Vector2(1000, 680);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            Priority = 0,
            ShowTooltip = () => ImGui.SetTooltip("PoseKit 设置"),
            Click = _ => plugin.ToggleConfigUi(),
        });
    }

    public void Dispose() { }

    public override void Draw()
    {
        PoseKitUi.PaintWindowBackground();
        using var theme = PoseKitUi.PushTheme();

        DrawHeader();
        DrawStatusLine();
        ImGui.Spacing();

        var avail = ImGui.GetContentRegionAvail();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var railWidth = avail.X < NarrowWindowWidth ? NarrowRailWidth : RailWidth;

        if (PoseKitUi.BeginCard("##PoseKitContent", new Vector2(avail.X - railWidth - spacing, avail.Y),
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            DrawPage();
        PoseKitUi.EndCard();

        ImGui.SameLine();
        if (PoseKitUi.BeginCard("##PoseKitRail", new Vector2(railWidth, avail.Y)))
            DrawRail();
        PoseKitUi.EndCard();
    }

    private void DrawHeader()
    {
        ImGui.SetWindowFontScale(1.3f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(PoseKitUi.Accent, "P O S E K I T");
        ImGui.SetWindowFontScale(1f);
        ImGui.SameLine(0, 8);
        ImGui.TextColored(PoseKitUi.Muted, $"v{Plugin.Version}");

        ImGui.SameLine(0, 24);
        if (PoseKitUi.PillTab("动画", page == Page.Animations, plugin.DiscoveredPoses.Count.ToString()))
            page = Page.Animations;
        ImGui.SameLine(0, 4);
        if (PoseKitUi.PillTab("预设", page == Page.Presets, plugin.PresetManager.Presets.Count.ToString()))
            page = Page.Presets;

        const string resyncLabel = "重新同步##PoseKitHeader重新同步";
        const string settingsLabel = "设置##PoseKitHeader设置";
        var freecamLabel = plugin.FreeCam.Enabled ? "自由镜头：开##PoseKitHeader自由镜头" : "自由镜头##PoseKitHeader自由镜头";
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        const float donateGap = 14f;
        var toolsWidth = PoseKitUi.ButtonWidth(resyncLabel) + PoseKitUi.ButtonWidth(freecamLabel) + PoseKitUi.ButtonWidth(settingsLabel) + spacing * 2
                         + donateGap + PoseKitUi.KofiButtonWidth();
        var toolsX = ImGui.GetWindowContentRegionMax().X - toolsWidth;
        PoseKitUi.SameLineIfFits(toolsWidth);
        if (ImGui.GetCursorPosX() < toolsX)
            ImGui.SetCursorPosX(toolsX);

        if (ImGui.Button(resyncLabel))
            plugin.EmoteSync.Sync();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Restart every nearby player's emote together, on your screen.");
        ImGui.SameLine();
        if (PoseKitUi.ToggleButton(freecamLabel, plugin.FreeCam.Enabled))
            plugin.FreeCam.Toggle();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(plugin.FreeCam.Status);
        ImGui.SameLine();
        if (ImGui.Button(settingsLabel))
            plugin.ToggleConfigUi();

        ImGui.SameLine(0, donateGap);
        PoseKitUi.KofiButton();
    }

    /// While freecam is on, its controls replace the color legend.
    private void DrawStatusLine()
    {
        PoseKitUi.DrawDependencyStatus(plugin);
        ImGui.SameLine(0, 16);
        DrawPairingStatus();

        if (plugin.FreeCam.Enabled)
        {
            const string hint = "自由镜头: WASD move · E/Q up/down · right-drag look";
            var hintWidth = ImGui.CalcTextSize(hint).X;
            PoseKitUi.SameLineIfFits(hintWidth + 16);
            RightAlign(hintWidth);
            ImGui.TextColored(PoseKitUi.Good, hint);
            return;
        }

        var legendWidth = LegendWidth();
        PoseKitUi.SameLineIfFits(legendWidth + 16);
        RightAlign(legendWidth);
        DrawLegend();
    }

    private static void RightAlign(float width)
    {
        var x = ImGui.GetWindowContentRegionMax().X - width;
        if (ImGui.GetCursorPosX() < x)
            ImGui.SetCursorPosX(x);
    }

    private void DrawPairingStatus()
    {
        var state = plugin.PairingState;
        if (state.Active && state.Peer is { } peer)
            PoseKitUi.StatusDot(PoseKitUi.Good, $"已与 {peer.Name.ToUpperInvariant()}");
        else if (state.PendingInvite != null)
            PoseKitUi.StatusDot(PoseKitUi.Info, "配对邀请 WAITING");
        else if (state.OutgoingInvite != null)
            PoseKitUi.StatusDot(PoseKitUi.Accent, "邀请已发送");
        else
            PoseKitUi.StatusDot(PoseKitUi.Muted, "未配对");
    }

    private void DrawRail()
    {
        PoseKitUi.CardTitle("配对");
        PoseKitUi.CardTitleRule();
        PairingPanel.Draw(plugin);

        ImGui.Spacing();
        PoseKitUi.SectionHeader("实时偏移");
        PresetButtonsPanel.DrawOffsets(plugin);

        ImGui.Spacing();
        PoseKitUi.SectionHeader("骨骼对齐");
        DrawBoneAlign();
    }

    private void DrawBoneAlign()
    {
        var configuration = plugin.Configuration;
        var labelWidth = ImGui.CalcTextSize("伙伴").X + ImGui.GetStyle().ItemSpacing.X;

        if (plugin.AutoAlign.CurrentEntry is { } known)
            DrawKnownAnimation(known);

        var self = configuration.BoneAlignSelf;
        if (DrawBodyPartCombo("自己", "##PoseKitBone对齐自己", labelWidth, ref self))
        {
            configuration.BoneAlignSelf = self;
            configuration.Save();
        }

        var partner = configuration.BoneAlignPartner;
        if (DrawBodyPartCombo("伙伴", "##PoseKitBone对齐伙伴", labelWidth, ref partner))
        {
            configuration.BoneAlignPartner = partner;
            configuration.Save();
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(PoseKitUi.Muted, "间距");
        ImGui.SameLine(labelWidth + ImGui.GetStyle().WindowPadding.X);
        ImGui.SetNextItemWidth(-1);
        var gap = configuration.BoneAlignGap;
        if (ImGui.SliderFloat("##PoseKitBone对齐间距", ref gap, 0f, 0.10f, "%.2fy"))
        {
            configuration.BoneAlignGap = gap;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("两部分之间保留的间距，避免彼此融合。");

        var autoAlign = configuration.AutoAlignFromMemory;
        if (ImGui.Checkbox("根据记忆自动对齐##PoseKitAuto对齐", ref autoAlign))
        {
            configuration.AutoAlignFromMemory = autoAlign;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("每次成功对齐都会记住当前动画中的相对朝向：你相对于\n" +
                             "your partner and where the parts meet. Next time you play it near your partner (or a targeted\n" +
                             "player), PoseKit turns and aligns you by itself.\n" +
                             "关闭后将停止自动对齐，但对齐结果仍会记住。");

        var align = plugin.BoneAlign;
        DrawQuickTurns(align);

        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(align.IsAligning))
        {
            if (PoseKitUi.WideButton(align.IsAligning ? "对齐中……##PoseKitBone对齐" : "对齐##PoseKitBone对齐"))
                align.Start(Bones.AlignRequest.FromConfiguration(configuration));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("重新同步s emotes, watches both parts for a moment, then moves you so they meet.\n" +
                             "保持当前朝向：请先使用上方按钮或实时偏移调整方向。");

        var hasKey = plugin.CurrentPlayContext != null;
        var rememberLabel = plugin.AutoAlign.CurrentEntry != null ? "更新记忆##PoseKitBone对齐记住" : "记住##PoseKitBone对齐记住";
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(align.IsAligning || !hasKey))
        {
            if (PoseKitUi.WideButton(rememberLabel))
                align.Start(Bones.AlignRequest.ForMeasure(configuration));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(hasKey
                ? "保存你当前的位置而不移动角色：你相对于伙伴的朝向以及\n" +
                  "where the parts meet. Use it after nudging the live offset by hand."
                : "Play an animation from the 动画 page (or one PoseKit can identify) to remember it.");

        if (align.Status.Length > 0)
            PoseKitUi.TextWrappedDisabled(align.Status);
    }

    private static readonly float[] QuickTurnDegrees = [-90f, 180f, 90f];

    /// -90 / 180 / +90 turns around the Self part, added to the bone-align correction.
    private void DrawQuickTurns(Bones.BoneAlignService align)
    {
        var posing = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer) != null;
        var rotationAvailable = plugin.OffsetEngine.RotationHookResolved;
        var why = !rotationAvailable ? "旋转 offset unavailable — hook didn't resolve this game version."
            : !posing ? "Start a pose or emote first."
            : align.IsAligning ? "Wait for the current alignment to finish."
            : "";

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonWidth = (ImGui.GetContentRegionAvail().X - spacing * (QuickTurnDegrees.Length - 1)) / QuickTurnDegrees.Length;
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(why.Length > 0))
        {
            for (var i = 0; i < QuickTurnDegrees.Length; i++)
            {
                if (i > 0) ImGui.SameLine();
                var degrees = QuickTurnDegrees[i];
                var label = MathF.Abs(degrees) >= 180f ? $"{MathF.Abs(degrees):0}°" : $"{degrees:+0;-0}°";
                if (ImGui.Button($"{label}##PoseKitQuickTurn{i}", new Vector2(buttonWidth, 0)))
                    align.QuickTurn(degrees);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(why.Length > 0 ? why : $"Turn {MathF.Abs(degrees):0} degrees around your {Bones.BodyParts.DisplayName(plugin.Configuration.Bone对齐自己).ToLowerInvariant()}.");
            }
        }
        if (!rotationAvailable)
            PoseKitUi.TextWrappedDisabled(why);
    }

    /// The remembered alignment for the playing animation, with a Forget button.
    private void DrawKnownAnimation(Bones.AlignmentEntry known)
    {
        const float pad = 8f;
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        // Content on the top channel, the panel behind it on the bottom one, drawn once its height is known.
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(start + new Vector2(pad, pad));
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - pad * 2);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(PoseKitUi.Good, "●");
        ImGui.SameLine(0, 5);
        ImGui.TextColored(PoseKitUi.Good, "已知动画");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{known.ModName} — {known.Option} ({known.Trigger})\n记住ed from your last successful 对齐 or 记住 on this animation.");

        const string forgetLabel = "忘记##PoseKitBone对齐忘记";
        var forgetWidth = ImGui.CalcTextSize("忘记").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine(width - pad * 2 - forgetWidth); // relative to the group, which starts pad in
        if (ImGui.SmallButton(forgetLabel))
            plugin.AlignmentMemory.Forget(known.Key);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("在再次对齐或记住之前，停止对该动画自动对齐。");

        ImGui.TextUnformatted(Bones.BodyParts.DisplayName(known.Self));
        ImGui.SameLine(0, 6);
        ImGui.TextColored(PoseKitUi.Muted, "to");
        ImGui.SameLine(0, 6);
        ImGui.TextUnformatted(Bones.BodyParts.DisplayName(known.Partner));

        var facing = known.RelativeYaw is { } yaw ? $"Facing {Bones.Bone对齐Service.FacingText(yaw)}" : "未保存朝向";
        var contact = known.ContactOffset is { } offset ? $"{offset.ToVector3().Length():0.00}y contact" : $"{known.间距:0.00}y gap";
        PoseKitUi.TextWrappedDisabled($"{facing} · {contact}");
        if (plugin.AutoAlign.Waiting.Length > 0)
            PoseKitUi.TextWrappedDisabled(plugin.AutoAlign.Waiting);

        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        var end = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + pad);

        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(PoseKitUi.Good with { W = 0.10f }), 6f);
        drawList.AddRect(start, end, ImGui.GetColorU32(PoseKitUi.Good with { W = 0.35f }), 6f);
        drawList.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
        ImGui.Dummy(new Vector2(width, 0));
        ImGui.Spacing();
    }

    private static bool DrawBodyPartCombo(string label, string id, float labelWidth, ref Bones.BodyPart value)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(PoseKitUi.Muted, label);
        ImGui.SameLine(labelWidth + ImGui.GetStyle().WindowPadding.X);
        ImGui.SetNextItemWidth(-1);

        var changed = false;
        if (ImGui.BeginCombo(id, Bones.BodyParts.DisplayName(value)))
        {
            foreach (var part in Bones.BodyParts.All)
            {
                if (ImGui.Selectable(Bones.BodyParts.DisplayName(part), part == value))
                {
                    value = part;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    private void DrawPage()
    {
        switch (page)
        {
            case Page.Animations:
                PenumbraPosePanel.DrawToolbar(plugin);
                DrawScrollRegion("##PoseKit动画Scroll", () => PenumbraPosePanel.Draw(plugin));
                break;

            case Page.Presets:
                PresetButtonsPanel.DrawToolbar(plugin);
                DrawScrollRegion("##PoseKit预设Scroll", () => PresetButtonsPanel.DrawPresets(plugin));
                break;
        }
    }

    private static void DrawScrollRegion(string id, Action draw)
    {
        if (ImGui.BeginChild(id, Vector2.Zero, false, ImGuiWindowFlags.None))
            draw();
        ImGui.EndChild();
    }

    private static readonly (Vector4 Color, string Label)[] LegendEntries =
    [
        (PoseKitUi.Accent, "你的选择"), (PoseKitUi.Info, "伙伴's pick"),
        (PoseKitUi.Good, "双方准备就绪"), (PoseKitUi.Bad, "冲突"),
    ];

    private const float LegendGap = 12f;

    private static float LegendWidth()
    {
        var width = 0f;
        foreach (var (_, label) in LegendEntries)
            width += ImGui.CalcTextSize("●").X + 5f + ImGui.CalcTextSize(label).X;
        return width + LegendGap * (LegendEntries.Length - 1);
    }

    private static void DrawLegend()
    {
        ImGui.BeginGroup();
        for (var i = 0; i < LegendEntries.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, LegendGap);
            PoseKitUi.StatusDot(LegendEntries[i].Color, LegendEntries[i].Label);
        }
        ImGui.EndGroup();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("配对时动画和预设高亮颜色的含义。");
    }
}
