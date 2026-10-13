using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using PoseKit.Pairing;

namespace PoseKit.Windows;

/// <summary>Pairing status and controls: invites, couple preset requests, both sides' picks, and
/// options.</summary>
public static class PairingPanel
{
    private static string inviteAddress = "";

    public static void Draw(Plugin plugin)
    {
        var state = plugin.PairingState;

        if (state.Active && state.Peer is { } peer)
        {
            DrawPaired(plugin, state, peer);
            return;
        }

        if (state.PendingInvite is { } pending)
        {
            ImGui.TextColored(PoseKitUi.Info, "配对邀请");
            ImGui.TextWrapped($"{pending.Sender} 想与你配对。");
            ImGui.Spacing();
            if (HalfButton("接受##PoseKitAcceptPairing", first: true))
                plugin.PairingListener.AcceptPending();
            if (HalfButton("忽略##PoseKitDismissPairing", first: false))
                state.DismissPendingInvite();
            return;
        }

        if (state.OutgoingInvite is { } outgoing)
        {
            ImGui.TextColored(PoseKitUi.Muted, "邀请已发送");
            ImGui.TextWrapped($"等待 {outgoing.Target} 接受邀请……");
            ImGui.Spacing();
            if (PoseKitUi.WideButton("取消邀请##PoseKitCancelInvite"))
                state.CancelOutgoingInvite();
            return;
        }

        ImGui.TextColored(PoseKitUi.Muted, "没有活动配对");
        ImGui.TextWrapped("与伙伴配对后，可排队姿势并一起播放双人预设。");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##PoseKitInviteAddress", "名字 姓氏@World", ref inviteAddress, 64);

        var valid = PartnerIdentity.TryParse(inviteAddress, out var target, out var error);
        using (ImRaii.Disabled(!valid))
        {
            if (PoseKitUi.WideButton("发送配对邀请##PoseKitSendInvite"))
            {
                plugin.PairingListener.InitiatePairing(target);
                inviteAddress = "";
            }
        }

        if (!valid && inviteAddress.Trim().Length > 0)
            ImGui.TextColored(PoseKitUi.Bad, error);

        ImGui.Spacing();
        PoseKitUi.TextWrappedDisabled("配对后，点击预设或动画会将其加入队列并为双方高亮；双方都选择后会一起播放。");
    }

    private static void DrawPaired(Plugin plugin, PairingState state, PartnerIdentity peer)
    {
        ImGui.TextColored(PoseKitUi.Good, "●");
        ImGui.SameLine(0, 6);
        ImGui.TextWrapped($"已与 {peer} 配对");

        var relay = plugin.CoupleRelayInbox;
        if (relay.Sender is { } relaySender && relay.PresetName is { } relayPresetName)
        {
            PoseKitUi.SectionHeader("收到的邀请");
            ImGui.PushStyleColor(ImGuiCol.Text, PoseKitUi.Info);
            ImGui.TextWrapped($"{relaySender.Name} 想与你一起播放“{relayPresetName}”。");
            ImGui.PopStyleColor();
            if (HalfButton("接受##PoseKitAcceptCoupleRelay", first: true))
            {
                if (relay.Accept() is { } captured)
                    plugin.ApplyCapturedPartnerState(captured);
            }
            if (HalfButton("拒绝##PoseKitDenyCoupleRelay", first: false))
                relay.Deny();
        }

        var outbox = plugin.CoupleRelayOutbox;
        if (outbox.PendingPresetName is { } outgoingName)
        {
            PoseKitUi.SectionHeader("发出的邀请");
            ImGui.PushStyleColor(ImGuiCol.Text, PoseKitUi.Info);
            ImGui.TextWrapped($"等待伙伴接受“{outgoingName}”……");
            ImGui.PopStyleColor();
            if (PoseKitUi.WideButton("取消##PoseKitCancelCoupleRelay"))
                outbox.Cancel();
        }

        PoseKitUi.SectionHeader("选择");
        var queue = plugin.CoupleQueueService;
        DrawPickRow(PoseKitUi.Accent, "你", queue.QueuedSelectionName);
        DrawPickRow(PoseKitUi.Info, "伙伴", queue.PartnerSelectionName);

        if (queue.PartnerSelectionName is { } theirs && queue.QueuedSelectionName == null)
        {
            ImGui.Spacing();
            DrawQuickPick(plugin, theirs);
        }

        PoseKitUi.SectionHeader("选项");
        var overrideEnabled = state.LocalOverrideEnabled;
        if (ImGui.Checkbox("覆盖队列##PoseKitOverrideQueue", ref overrideEnabled))
        {
            state.SetLocalOverrideEnabled(overrideEnabled);
            plugin.PairingListener.SendOverrideToggle(overrideEnabled);
        }

        if (state.MutualOverrideActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, PoseKitUi.Good);
            ImGui.TextWrapped("已启用覆盖队列——选择第二项时会播放你的第一项并强制伙伴选择它。");
            ImGui.PopStyleColor();
        }
        else if (state.LocalOverrideEnabled)
            PoseKitUi.TextWrappedDisabled("等待伙伴也启用覆盖队列；在此之前，第二次点击只会替换你自己的选择。");

        // One-sided; nothing is sent to the partner.
        var soloPlayEnabled = state.SoloPlayEnabled;
        if (ImGui.Checkbox("单人播放##PoseKitSoloPlay", ref soloPlayEnabled))
            state.SetSoloPlayEnabled(soloPlayEnabled);

        if (state.SoloPlayEnabled)
            PoseKitUi.TextWrappedDisabled("点击后仅立即为你播放，不会排队、强制或发送给伙伴。");

        ImGui.Spacing();
        PoseKitUi.TextWrappedDisabled("点击任意预设或动画加入队列——双方都选择后会一起播放。");

        ImGui.Spacing();
        if (PoseKitUi.WideButton("解除配对##PoseKitUnpair"))
            plugin.PairingListener.Unpair();
    }

    private static void DrawPickRow(Vector4 color, string who, string? pick)
    {
        ImGui.TextColored(color, "●");
        ImGui.SameLine(0, 6);
        ImGui.TextColored(PoseKitUi.Muted, who);
        ImGui.SameLine(0, 8);
        if (pick != null)
            ImGui.TextWrapped(pick);
        else
            ImGui.TextColored(PoseKitUi.Muted, "暂无选择");
    }

    /// One of a pair of buttons splitting the current row evenly.
    private static bool HalfButton(string label, bool first)
    {
        if (!first) ImGui.SameLine();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = first ? (ImGui.GetContentRegionAvail().X - spacing) / 2f : ImGui.GetContentRegionAvail().X;
        return ImGui.Button(label, new Vector2(width, 0));
    }

    /// This side's matching preset or animation for the partner's pick, if any.
    private static void DrawQuickPick(Plugin plugin, string partnerSelectionName)
    {
        var preset = plugin.PresetManager.Presets.FirstOrDefault(p => p.Name == partnerSelectionName);
        if (preset != null)
        {
            PresetButtonsPanel.DrawPresetEntry(plugin, preset);
            return;
        }

        if (!PenumbraPosePanel.TryDrawQuickTriggerButtons(plugin, partnerSelectionName))
            PoseKitUi.TextWrappedDisabled($"你的列表中没有找到与“{partnerSelectionName}”匹配的预设或动画。");
    }
}
